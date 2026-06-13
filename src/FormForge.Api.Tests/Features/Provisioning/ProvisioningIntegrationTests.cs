using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.Provisioning;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class ProvisioningIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public ProvisioningIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();

        // Order matters: menus references component_schemas via FK fk_menus_bound_designer (SetNull),
        // so we truncate menus FIRST. component_schema_versions cascades from component_schemas, but
        // we name it explicitly so the truncate is order-deterministic across PG versions.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE menu_role_assignments, menus, component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users, schema_audit_log RESTART IDENTITY CASCADE;");

        // Drop any dynamically-provisioned tables from previous test runs. These tables
        // live outside the static EF schema and the TRUNCATE above does not reach them.
        // The list below MUST be kept in sync with the static schema; anything not in
        // the allow-list is assumed to be a dynamic table left behind by Story 5.3.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN
                    SELECT tablename FROM pg_tables
                    WHERE schemaname = 'public'
                      AND tablename NOT IN (
                          'users', 'refresh_tokens', 'roles', 'role_permissions', 'user_roles',
                          'component_schemas', 'component_schema_versions',
                          'menus', 'menu_role_assignments',
                          'schema_audit_log',
                          '__EFMigrationsHistory'
                      )
                LOOP
                    EXECUTE 'DROP TABLE IF EXISTS ' || quote_ident(r.tablename) || ' CASCADE';
                END LOOP;
            END $$;
            """);

        await ReseedSystemRolesAsync(db);
        await SeedTestUsersAsync(db);

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // ---------- PUT /api/admin/menus/{id}/binding ----------

    [Fact]
    public async Task BindDesigner_ValidPublishedVersion_Returns202AndProvisionsAsync()
    {
        // AC-1 happy path: bind to a Published version, observe 202, then poll
        // until the BackgroundService stub flips status to Success.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Incidents", 0);
        await CreateAndPublishDesignerAsync(token, "incident_report");

        using var response = await PutBindingAsync(token, menuId, "incident_report", 1);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        var menu = await GetMenuAsync(token, menuId);
        Assert.NotNull(menu);
        Assert.Equal("incident_report", menu!.DesignerId);
        Assert.Equal(1, menu.BoundVersion);
        Assert.Null(menu.ProvisioningError);
    }

    // Regression (deploy-only bug): dataset Preview executes as the sandboxed,
    // least-privilege `formforge_preview` role. Designer-provisioned tables are created
    // AFTER the dataset-foundation migration's one-time GRANT, so unless DdlEmitter grants
    // the preview role SELECT on each new table, preview throws "42501: permission denied"
    // in deployment — yet passes in dev/integration, where the preview pool is pointed at
    // the superuser connection (see DatasetPreviewTests / AppHost.cs). The other dataset
    // tests share that blind spot; this one connects AS the real preview role to assert the
    // grant actually reaches a freshly provisioned table while the sensitive floor holds.
    [Fact]
    public async Task BindDesigner_GrantsProvisionedTableToPreviewRoleAsync()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "PreviewGrant", 0);
        await CreateAndPublishDesignerAsync(token, "preview_grant_probe");

        using (var response = await PutBindingAsync(token, menuId, "preview_grant_probe", 1))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        // The migration creates formforge_preview without a password; give it one so the
        // test can authenticate AS the role (in deployment the startup ALTER ROLE step does
        // this from DatasetManager:PreviewRolePassword).
        await using (var su = new NpgsqlConnection(_postgres.ConnectionString))
        {
            await su.OpenAsync();
            await using var alter = new NpgsqlCommand(
                "ALTER ROLE formforge_preview WITH LOGIN PASSWORD 'preview_test_pw';", su);
            await alter.ExecuteNonQueryAsync();
        }

        var previewConnectionString = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            Username = "formforge_preview",
            Password = "preview_test_pw",
        }.ConnectionString;

        await using var preview = new NpgsqlConnection(previewConnectionString);
        await preview.OpenAsync();

        // The fix: the freshly provisioned table must be readable by the sandboxed role.
        // Without the DdlEmitter grant this SELECT throws 42501.
        await using (var readProvisioned = new NpgsqlCommand(
            "SELECT count(*) FROM preview_grant_probe;", preview))
        {
            var count = await readProvisioned.ExecuteScalarAsync();
            Assert.Equal(0L, count); // empty but readable — no permission error
        }

        // The sandbox floor still holds: sensitive user columns remain denied. (users is
        // exposed for joins via a column-level grant on id/display_name/email/is_active —
        // see RestrictPreviewRoleUsersColumns — so count(*) is allowed, but reading a
        // sensitive column like password_hash still errors with 42501.)
        await using (var readHash = new NpgsqlCommand("SELECT password_hash FROM users;", preview))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => readHash.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState); // 42501
        }
    }

    // FR-54 AC-3 — binding a VIEW-mode designer skips provisioning entirely:
    // status is NotApplicable, no table is created, and no job runs.
    [Fact]
    public async Task BindDesigner_ViewMode_SkipsProvisioning_StatusNotApplicableAsync()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "ViewMenu", 0);
        await CreateAndPublishDesignerAsync(token, "view_dash", "VIEW");

        using var response = await PutBindingAsync(token, menuId, "view_dash", 1);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The VIEW gate commits synchronously before returning — no polling needed.
        var menu = await GetMenuAsync(token, menuId);
        Assert.NotNull(menu);
        Assert.Equal("view_dash", menu!.DesignerId);
        Assert.Equal(1, menu.BoundVersion);
        Assert.Equal("NotApplicable", menu.ProvisioningStatus);
        Assert.Null(menu.ProvisioningError);

        // No table is ever provisioned for a VIEW designer.
        Assert.False(await TableExistsInPostgresAsync("view_dash"));
    }

    [Fact]
    public async Task BindDesigner_DraftVersion_Returns422_VersionNotPublished()
    {
        // AC-2: only Published versions are bindable. v1 is left in Draft so the
        // service-layer Status check fires VERSION_NOT_PUBLISHED.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Drafts", 0);
        await CreateDesignerViaApiAsync(token, "draft_only");
        // Save a v1 but don't publish.
        await PostVersionAsync(token, "draft_only");

        using var response = await PutBindingAsync(token, menuId, "draft_only", 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("VERSION_NOT_PUBLISHED", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BindDesigner_ArchivedVersion_Returns422_VersionNotPublished()
    {
        // AC-2: archived versions are also rejected (Status != "Published").
        // Publish v1, then explicitly archive it; binding to the archived v1 fails.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "ArchivedTarget", 0);
        await CreateAndPublishDesignerAsync(token, "archived_v1");
        await PutVersionStatusAsync(token, "archived_v1", 1, "Archived");  // explicit archive

        using var response = await PutBindingAsync(token, menuId, "archived_v1", 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("VERSION_NOT_PUBLISHED", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BindDesigner_MenuNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateAndPublishDesignerAsync(token, "exists_designer");

        var missingMenuId = Guid.NewGuid();
        using var response = await PutBindingAsync(token, missingMenuId, "exists_designer", 1);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("MENU_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BindDesigner_UnknownDesignerId_Returns422_DesignerVersionNotFound()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "NoDesignerYet", 0);

        using var response = await PutBindingAsync(token, menuId, "does_not_exist", 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("DESIGNER_VERSION_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BindDesigner_Unauthenticated_Returns401()
    {
        // No need to seed the menu — the auth check fires before any service code.
        var menuId = Guid.NewGuid();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/admin/menus/{menuId}/binding")
        {
            Content = JsonContent.Create(new { designerId = "any_designer", version = 1 }),
        };
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BindDesigner_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");
        var menuId = Guid.NewGuid();
        using var response = await PutBindingAsync(token, menuId, "any_designer", 1);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BindDesigner_RebindToNewVersion_Returns202AndUpdatesBoundVersion()
    {
        // AC-5: re-binding to a new version updates BoundVersion and reruns the pipeline.
        // Regression guard: a second PUT /binding must reset ProvisioningStatus to Pending,
        // enqueue a new job, and the BackgroundService must process it to Success with the
        // updated BoundVersion — not silently retain the old binding values.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "RebindTarget", 0);
        await CreateAndPublishDesignerAsync(token, "rebind_designer");

        // Bind v1 and wait for Success.
        using (var bindV1 = await PutBindingAsync(token, menuId, "rebind_designer", 1))
        {
            Assert.Equal(HttpStatusCode.Accepted, bindV1.StatusCode);
        }
        var firstStatus = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", firstStatus);

        // Create and publish v2.
        await PostVersionAsync(token, "rebind_designer");
        await PutVersionStatusAsync(token, "rebind_designer", 2, "Published");

        // Bind v2 and wait for Success.
        using (var bindV2 = await PutBindingAsync(token, menuId, "rebind_designer", 2))
        {
            Assert.Equal(HttpStatusCode.Accepted, bindV2.StatusCode);
        }
        var secondStatus = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", secondStatus);

        var menu = await GetMenuAsync(token, menuId);
        Assert.NotNull(menu);
        Assert.Equal("rebind_designer", menu!.DesignerId);
        Assert.Equal(2, menu.BoundVersion);
        Assert.Null(menu.ProvisioningError);
    }

    // ---------- POST /api/admin/menus/{id}/binding/retry ----------

    [Fact]
    public async Task RetryBinding_MenuNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var missingMenuId = Guid.NewGuid();
        using var response = await PostRetryAsync(token, missingMenuId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("MENU_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RetryBinding_MenuWithNoBinding_Returns422_NoBinding()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Unbound", 0);

        using var response = await PostRetryAsync(token, menuId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("MENU_NO_BINDING", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RetryBinding_ExistingBinding_Returns202AndReprovisions()
    {
        // After the first bind reaches Success, a retry re-enqueues the job and
        // the BackgroundService runs it again. The new UpdatedAt strictly advances
        // past the prior Success timestamp, proving the second pipeline run; this
        // assertion is race-free (no need to observe the transient Pending state).
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "Retryable", 0);
        await CreateAndPublishDesignerAsync(token, "retry_designer");

        using (var bindResp = await PutBindingAsync(token, menuId, "retry_designer", 1))
        {
            Assert.Equal(HttpStatusCode.Accepted, bindResp.StatusCode);
        }
        var firstStatus = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", firstStatus);
        var afterFirst = await GetMenuAsync(token, menuId);
        var firstUpdatedAt = afterFirst!.UpdatedAt;
        Assert.NotNull(firstUpdatedAt);

        using (var retryResp = await PostRetryAsync(token, menuId))
        {
            Assert.Equal(HttpStatusCode.Accepted, retryResp.StatusCode);
        }
        var secondStatus = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", secondStatus);

        var afterRetry = await GetMenuAsync(token, menuId);
        Assert.NotNull(afterRetry);
        // DesignerId/BoundVersion preserved across the retry (AC-4 "without changing the binding values").
        Assert.Equal("retry_designer", afterRetry!.DesignerId);
        Assert.Equal(1, afterRetry.BoundVersion);
        Assert.NotNull(afterRetry.UpdatedAt);
        Assert.True(afterRetry.UpdatedAt > firstUpdatedAt,
            $"Expected UpdatedAt to advance after retry; first={firstUpdatedAt:O}, after={afterRetry.UpdatedAt:O}");
    }

    // ---------- GET /api/admin/menus/{id}/binding-diff ----------

    [Fact]
    public async Task GetBindingDiff_MenuNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var missingMenuId = Guid.NewGuid();
        using var response = await GetBindingDiffAsync(token, missingMenuId, targetVersion: 2);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("MENU_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetBindingDiff_MenuWithBinding_Returns200WithDiff()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DiffMenu", 0);
        await CreateAndPublishDesignerAsync(token, "diff_designer");

        using (var bindResp = await PutBindingAsync(token, menuId, "diff_designer", 1))
        {
            Assert.Equal(HttpStatusCode.Accepted, bindResp.StatusCode);
        }
        await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));

        using var response = await GetBindingDiffAsync(token, menuId, targetVersion: 2);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<BindingDiffDto>();
        Assert.NotNull(body);
        Assert.NotNull(body!.CurrentBinding);
        Assert.Equal("diff_designer", body.CurrentBinding!.DesignerId);
        Assert.Equal(1, body.CurrentBinding.Version);
        Assert.Equal(2, body.TargetVersion);
        // Story 5.2 stub returns empty arrays + one placeholder DDL line. Locks
        // the shape so Story 5.6 can fill it in without changing the contract.
        Assert.Empty(body.ColumnsToAdd);
        Assert.Empty(body.ColumnsAlreadyPresent);
        Assert.Empty(body.OrphanedColumns);
        Assert.Empty(body.WillTriggerChildProvisioning);
        Assert.Single(body.EstimatedDdl);
    }

    [Fact]
    public async Task GetBindingDiff_MenuWithNoBinding_Returns404()
    {
        // Service returns null when designerId or boundVersion is null; the handler
        // surfaces that as MENU_NOT_FOUND (no current binding to diff against).
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DiffUnbound", 0);

        using var response = await GetBindingDiffAsync(token, menuId, targetVersion: 2);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("MENU_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Story 5.3 — real DDL provisioning ----------

    [Fact]
    public async Task ProvisionNewTable_EmptyDesigner_CreatesTableWithSystemColumnsOnly()
    {
        // AC-1: empty Stack root → only system columns in the created table.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "EmptyDesignerMenu", 0);
        await CreateAndPublishDesignerAsync(token, "empty_designer");

        using (var bind = await PutBindingAsync(token, menuId, "empty_designer", 1))
        {
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        }

        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        Assert.True(await TableExistsInPostgresAsync("empty_designer"));
        var cols = await GetTableColumnsAsync("empty_designer");
        var colNames = cols.Select(c => c.Name).ToHashSet();
        // System columns
        Assert.Contains("id", colNames);
        Assert.Contains("created_at", colNames);
        Assert.Contains("created_by", colNames);
        Assert.Contains("updated_at", colNames);
        Assert.Contains("updated_by", colNames);
        Assert.Contains("is_deleted", colNames);
        Assert.Contains("cascade_event_id", colNames);
        Assert.Equal(7, cols.Count);   // only system columns, no user columns
    }

    [Fact]
    public async Task ProvisionNewTable_WithTextAndNumericFields_CreatesCorrectPgColumns()
    {
        // AC-1, AC-2: TextInput → TEXT (nullable), NumberInput → NUMERIC (nullable),
        // TextArea → TEXT (nullable). All dynamic columns must accept NULL.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "TextNumericMenu", 0);

        var rootElement = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "full_name" }, children = Array.Empty<object>() },
                new { id = "f2", type = "NumberInput",
                      properties = new { fieldKey = "age" }, children = Array.Empty<object>() },
                new { id = "f3", type = "TextArea",
                      properties = new { fieldKey = "notes" }, children = Array.Empty<object>() },
            },
        };
        var publishedVersion = await CreateAndPublishDesignerWithFieldsAsync(token, "text_numeric_fields", rootElement);

        using (var bind = await PutBindingAsync(token, menuId, "text_numeric_fields", publishedVersion))
        {
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        }

        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        var cols = await GetTableColumnsAsync("text_numeric_fields");
        var colMap = cols.ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
        Assert.Equal("text", colMap["full_name"]);
        Assert.Equal("numeric", colMap["age"]);
        Assert.Equal("text", colMap["notes"]);
        // AC-2: every user column accepts NULL — INSERT all-NULL row must not throw.
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO text_numeric_fields (full_name, age, notes) VALUES (NULL, NULL, NULL)";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProvisionNewTable_WithBooleanAndDateTimeFields_CreatesCorrectPgColumns()
    {
        // AC-1: Checkbox → BOOLEAN, DateTimePicker → TIMESTAMPTZ, Dropdown/ColorPicker/File → TEXT.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "BoolDateMenu", 0);

        var rootElement = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "Checkbox",
                      properties = new { fieldKey = "is_active" }, children = Array.Empty<object>() },
                new { id = "f2", type = "DateTimePicker",
                      properties = new { fieldKey = "occurred_at" }, children = Array.Empty<object>() },
                new { id = "f3", type = "Dropdown",
                      properties = new { fieldKey = "category" }, children = Array.Empty<object>() },
                new { id = "f4", type = "ColorPicker",
                      properties = new { fieldKey = "bg_color" }, children = Array.Empty<object>() },
                new { id = "f5", type = "File",
                      properties = new { fieldKey = "attachment" }, children = Array.Empty<object>() },
            },
        };
        var publishedVersion = await CreateAndPublishDesignerWithFieldsAsync(token, "bool_date_fields", rootElement);

        using (var bind = await PutBindingAsync(token, menuId, "bool_date_fields", publishedVersion))
        {
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        }

        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        var cols = await GetTableColumnsAsync("bool_date_fields");
        var colMap = cols.ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
        Assert.Equal("boolean", colMap["is_active"]);
        Assert.Equal("timestamp with time zone", colMap["occurred_at"]);
        Assert.Equal("text", colMap["category"]);
        Assert.Equal("text", colMap["bg_color"]);
        Assert.Equal("text", colMap["attachment"]);
    }

    [Fact]
    public async Task ProvisionNewTable_StructuralAndUiOnlyComponents_ProduceNoColumns()
    {
        // AC-1: Stack, Row, Label, Button, Image (static display) → no user columns;
        // only 7 system columns.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "StructuralMenu", 0);

        var rootElement = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "r1",
                    type = "Row",
                    properties = new { },
                    children = new object[]
                    {
                        new { id = "l1", type = "Label",
                              properties = new { label = "Title" }, children = Array.Empty<object>() },
                        new { id = "b1", type = "Button",
                              properties = new { label = "Submit" }, children = Array.Empty<object>() },
                        // Static display image — has a `src`, no fieldKey, produces no column.
                        new { id = "img1", type = "Image",
                              properties = new { src = "logo.png" }, children = Array.Empty<object>() },
                    },
                },
            },
        };
        var publishedVersion = await CreateAndPublishDesignerWithFieldsAsync(token, "structural_only", rootElement);

        using (var bind = await PutBindingAsync(token, menuId, "structural_only", publishedVersion))
        {
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        }

        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        Assert.True(await TableExistsInPostgresAsync("structural_only"));
        var cols = await GetTableColumnsAsync("structural_only");
        Assert.Equal(7, cols.Count);   // only system columns
    }

    [Fact]
    public async Task ProvisionNewTable_AuditLogRowAppended_OnCreate()
    {
        // AC-5: after successful CREATE TABLE, schema_audit_log has a row with
        // ddl_operation = "CREATE", from_version = NULL, to_version = 1.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "AuditMenu", 0);
        await CreateAndPublishDesignerAsync(token, "audit_designer");

        using (var bind = await PutBindingAsync(token, menuId, "audit_designer", 1))
        {
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        }

        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        // Query schema_audit_log directly via the fixture connection.
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ddl_operation, from_version, to_version, actor_id, correlation_id
                FROM schema_audit_log
                WHERE designer_id = 'audit_designer'
                ORDER BY created_at DESC
                LIMIT 1
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Expected a row in schema_audit_log");
            Assert.Equal("CREATE", reader.GetString(0));
            Assert.True(await reader.IsDBNullAsync(1), "from_version should be NULL for CREATE");
            Assert.Equal(1, reader.GetInt32(2));
            // correlation_id is a ULID (26 chars)
            Assert.Equal(26, reader.GetString(4).Length);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProvisionNewTable_IdempotentWhenTableExists_NoColumnDuplicated()
    {
        // AC-4: binding the same version twice → the second run succeeds (no error),
        // does not duplicate columns. The first bind issues CREATE; the retry falls
        // through to AddMissingColumns, which finds no missing columns and no-ops.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "IdempotentMenu", 0);
        await CreateAndPublishDesignerAsync(token, "idempotent_designer");

        // First bind → CREATE TABLE.
        using (var bind = await PutBindingAsync(token, menuId, "idempotent_designer", 1))
        {
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        }
        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        // Second bind (retry path) → falls through to AddMissingColumns, no-op.
        using (var retry = await PostRetryAsync(token, menuId))
        {
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        }
        var status2 = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status2);

        // Table should still have exactly 7 system columns (no duplicates).
        var cols = await GetTableColumnsAsync("idempotent_designer");
        Assert.Equal(7, cols.Count);
    }

    [Fact]
    public async Task ProvisionNewTable_SchemaRegistryPopulated_AfterSuccessfulProvisioning()
    {
        // AC-5: after provisioning, ISchemaRegistry can retrieve the entry for (designerId, version).
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "RegistryMenu", 0);
        await CreateAndPublishDesignerAsync(token, "registry_designer");

        using (var bind = await PutBindingAsync(token, menuId, "registry_designer", 1))
        {
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        }

        var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
        Assert.Equal("Success", status);

        // Access ISchemaRegistry from the test host's service container.
        using var scope = _factory!.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISchemaRegistry>();
        var entry = registry.TryGet("registry_designer", 1);
        Assert.NotNull(entry);
        Assert.Equal("registry_designer", entry!.DesignerId);
        Assert.Equal(1, entry.Version);
    }

    // ---------- Story 5.4 — schema evolution (ALTER TABLE ADD COLUMN) ----------

    [Fact]
    public async Task EvolveSchema_NewVersionWithAdditionalField_ColumnAdded()
    {
        // AC-1: binding to a newer version that has an extra field adds the column.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "EvolveMenu1", 0);

        var rootV1 = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "f1", type = "TextInput", properties = new { fieldKey = "first_name" },
                  children = Array.Empty<object>() },
            new { id = "f2", type = "TextInput", properties = new { fieldKey = "last_name" },
                  children = Array.Empty<object>() },
        }};
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "evolve_schema_1", rootV1);
        using (var bind1 = await PutBindingAsync(token, menuId, "evolve_schema_1", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // v3 adds email field
        var rootV2 = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "f1", type = "TextInput", properties = new { fieldKey = "first_name" },
                  children = Array.Empty<object>() },
            new { id = "f2", type = "TextInput", properties = new { fieldKey = "last_name" },
                  children = Array.Empty<object>() },
            new { id = "f3", type = "TextInput", properties = new { fieldKey = "email" },
                  children = Array.Empty<object>() },
        }};
        await PostVersionWithRootAsync(token, "evolve_schema_1", rootV2);
        await PutVersionStatusAsync(token, "evolve_schema_1", 3, "Published");

        using (var bind2 = await PutBindingAsync(token, menuId, "evolve_schema_1", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var cols = await GetTableColumnsAsync("evolve_schema_1");
        var colNames = cols.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("first_name", colNames);
        Assert.Contains("last_name", colNames);
        Assert.Contains("email", colNames);            // newly added column
        Assert.Equal(10, cols.Count);                  // 7 system + 3 user columns
    }

    [Fact]
    public async Task EvolveSchema_OrphanedColumnsRetained_NeverDropped()
    {
        // AC-2: columns absent from the new designer version are never dropped.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "EvolveMenu2", 0);

        var rootWithTwo = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "f1", type = "TextInput", properties = new { fieldKey = "keep_col" },
                  children = Array.Empty<object>() },
            new { id = "f2", type = "TextInput", properties = new { fieldKey = "orphan_col" },
                  children = Array.Empty<object>() },
        }};
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "evolve_schema_2", rootWithTwo);
        using (var bind1 = await PutBindingAsync(token, menuId, "evolve_schema_2", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // v3 removes orphan_col from the schema
        var rootWithOne = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "f1", type = "TextInput", properties = new { fieldKey = "keep_col" },
                  children = Array.Empty<object>() },
        }};
        await PostVersionWithRootAsync(token, "evolve_schema_2", rootWithOne);
        await PutVersionStatusAsync(token, "evolve_schema_2", 3, "Published");

        using (var bind2 = await PutBindingAsync(token, menuId, "evolve_schema_2", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var cols = await GetTableColumnsAsync("evolve_schema_2");
        var colNames = cols.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("keep_col", colNames);
        Assert.Contains("orphan_col", colNames);   // must NOT be dropped (AC-2)
        Assert.Equal(9, cols.Count);               // 7 system + 2 user columns (keep_col + orphan_col)

        // Verify the audit log records orphan_col in the columnsDiff JSON (AC-2).
        var conn2 = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn2.OpenAsync();
            using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = """
                SELECT column_diff
                FROM schema_audit_log
                WHERE designer_id = 'evolve_schema_2'
                  AND ddl_operation = 'ALTER'
                ORDER BY created_at DESC
                LIMIT 1
                """;
            var rawDiff = (string?)await cmd2.ExecuteScalarAsync();
            Assert.False(string.IsNullOrWhiteSpace(rawDiff), "column_diff must be non-null for ALTER rows");
            using var diffDoc = JsonDocument.Parse(rawDiff!);
            Assert.True(diffDoc.RootElement.TryGetProperty("orphanedColumns", out var orphanedEl));
            var orphanedNames = orphanedEl.EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("orphan_col", orphanedNames); // AC-2: orphaned column name recorded in diff
        }
        finally
        {
            await conn2.DisposeAsync();
        }
    }

    [Fact]
    public async Task EvolveSchema_AuditLogRecordsAlterWithCorrectFromAndToVersion()
    {
        // AC-4: fromVersion = previous BoundVersion; toVersion = new version; ddlOperation = "ALTER".
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "EvolveMenu3", 0);

        var rootV1 = new { id = "root", type = "Stack", properties = new { },
            children = new object[] {
                new { id = "f1", type = "TextInput", properties = new { fieldKey = "note" },
                      children = Array.Empty<object>() }
            }};
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "evolve_schema_3", rootV1);
        using (var bind1 = await PutBindingAsync(token, menuId, "evolve_schema_3", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var rootV2 = new { id = "root", type = "Stack", properties = new { },
            children = new object[] {
                new { id = "f1", type = "TextInput", properties = new { fieldKey = "note" },
                      children = Array.Empty<object>() },
                new { id = "f2", type = "NumberInput", properties = new { fieldKey = "score" },
                      children = Array.Empty<object>() },
            }};
        await PostVersionWithRootAsync(token, "evolve_schema_3", rootV2);
        await PutVersionStatusAsync(token, "evolve_schema_3", 3, "Published");

        using (var bind2 = await PutBindingAsync(token, menuId, "evolve_schema_3", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ddl_operation, from_version, to_version, columns_added, column_diff,
                       actor_id, correlation_id
                FROM schema_audit_log
                WHERE designer_id = 'evolve_schema_3'
                  AND ddl_operation = 'ALTER'
                ORDER BY created_at DESC
                LIMIT 1
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Expected an ALTER row in schema_audit_log");
            Assert.Equal("ALTER", reader.GetString(0));
            Assert.Equal(v1, reader.GetInt32(1));         // fromVersion = previous BoundVersion (v1 = 2)
            Assert.Equal(3, reader.GetInt32(2));           // toVersion = 3
            // columns_added is a text[] — verify score was added
            var columnsAdded = (string[]?)reader.GetValue(3);
            Assert.NotNull(columnsAdded);
            Assert.Contains("score", columnsAdded!);
            // column_diff is JSON TEXT — verify all three required properties (AC-4)
            var columnDiff = reader.GetString(4);
            Assert.False(string.IsNullOrWhiteSpace(columnDiff));
            using var diffDoc = JsonDocument.Parse(columnDiff);
            Assert.True(diffDoc.RootElement.TryGetProperty("existingUserColumns", out var existingEl));
            Assert.Contains("note", existingEl.EnumerateArray().Select(e => e.GetString()));
            Assert.True(diffDoc.RootElement.TryGetProperty("addedColumns", out _));
            Assert.True(diffDoc.RootElement.TryGetProperty("orphanedColumns", out var orphanedEl));
            Assert.Empty(orphanedEl.EnumerateArray()); // no columns removed in this evolution
            // actorId and correlationId must be present on the ALTER row (AC-4)
            Assert.False(await reader.IsDBNullAsync(5), "actor_id must be non-null on an authenticated ALTER");
            var correlationId = reader.GetString(6);
            Assert.Equal(26, correlationId.Length); // ULID is always 26 characters
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task EvolveSchema_SchemaRegistryPopulatedForNewVersion()
    {
        // AC-5: ISchemaRegistry has an entry for (designerId, newVersion) after evolution.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "EvolveMenu4", 0);

        var rootV1 = new { id = "root", type = "Stack", properties = new { }, children = new object[]
            { new { id = "f1", type = "TextInput", properties = new { fieldKey = "val" },
                    children = Array.Empty<object>() } }};
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "evolve_schema_4", rootV1);
        using (var bind1 = await PutBindingAsync(token, menuId, "evolve_schema_4", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var rootV2 = new { id = "root", type = "Stack", properties = new { }, children = new object[]
            { new { id = "f1", type = "TextInput", properties = new { fieldKey = "val" },
                    children = Array.Empty<object>() },
              new { id = "f2", type = "NumberInput", properties = new { fieldKey = "qty" },
                    children = Array.Empty<object>() } }};
        await PostVersionWithRootAsync(token, "evolve_schema_4", rootV2);
        await PutVersionStatusAsync(token, "evolve_schema_4", 3, "Published");

        using (var bind2 = await PutBindingAsync(token, menuId, "evolve_schema_4", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var scope = _factory!.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISchemaRegistry>();

        var entryV1 = registry.TryGet("evolve_schema_4", v1);
        Assert.NotNull(entryV1);    // old version entry must still exist

        var entryV2 = registry.TryGet("evolve_schema_4", 3);
        Assert.NotNull(entryV2);
        Assert.Equal("evolve_schema_4", entryV2!.DesignerId);
        Assert.Equal(3, entryV2.Version);
        Assert.Equal(2, entryV2.Columns.Count);    // val + qty
    }

    [Fact]
    public async Task EvolveSchema_SpaFormatComponentTypes_MapToCorrectPgTypes()
    {
        // Regression for ComponentTypeMapper SPA-name fix (Task 1).
        // Real SPA data uses "Text Input", "Number Input" etc. in RootElement JSON.
        // Before the fix these would fall through to JSONB; after the fix they map correctly.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "SpaFormatMenu", 0);

        var rootElement = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "f1", type = "Text Input",      // SPA format for TextInput
                  properties = new { fieldKey = "full_name" }, children = Array.Empty<object>() },
            new { id = "f2", type = "Number Input",    // SPA format for NumberInput
                  properties = new { fieldKey = "age" }, children = Array.Empty<object>() },
            new { id = "f3", type = "DateTime Picker", // SPA format for DateTimePicker
                  properties = new { fieldKey = "dob" }, children = Array.Empty<object>() },
            new { id = "f4", type = "Color Picker",    // SPA format for ColorPicker
                  properties = new { fieldKey = "color" }, children = Array.Empty<object>() },
        }};
        var publishedVersion = await CreateAndPublishDesignerWithFieldsAsync(token, "spa_format_test", rootElement);

        using (var bind = await PutBindingAsync(token, menuId, "spa_format_test", publishedVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var cols = await GetTableColumnsAsync("spa_format_test");
        var colMap = cols.ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
        Assert.Equal("text",                    colMap["full_name"]);  // Text Input → TEXT (not JSONB)
        Assert.Equal("numeric",                 colMap["age"]);        // Number Input → NUMERIC
        Assert.Equal("timestamp with time zone",colMap["dob"]);        // DateTime Picker → TIMESTAMPTZ
        Assert.Equal("text",                    colMap["color"]);      // Color Picker → TEXT
    }

    // ---------- Story 5.5 — child table provisioning + cycle detection ----------

    [Fact]
    public async Task ProvisionWithRepeater_CreatesChildTableWithFkColumnAndIndex()
    {
        // AC-1/AC-2/AC-3 happy path: parent Repeater → child designer triggers child
        // table creation in the same transaction, with a UUID FK column and an index.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "RepParentMenu", 0);

        // Child designer (must be Published BEFORE parent is bound).
        var childRoot = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "cf1", type = "TextInput", properties = new { fieldKey = "child_field" },
                  children = Array.Empty<object>() },
        }};
        await CreateAndPublishDesignerWithFieldsAsync(token, "repeater_child", childRoot);

        // Parent designer with Repeater → repeater_child + one TextInput.
        var parentRoot = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "pf1", type = "TextInput", properties = new { fieldKey = "parent_field" },
                  children = Array.Empty<object>() },
            new { id = "pr1", type = "Repeater", properties = new { rowDesignerId = "repeater_child", fieldKey = "child_rows" },
                  children = Array.Empty<object>() },
        }};
        var parentVersion = await CreateAndPublishDesignerWithFieldsAsync(
            token, "repeater_parent", parentRoot);

        using (var bind = await PutBindingAsync(token, menuId, "repeater_parent", parentVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // Parent table exists with system + parent_field.
        Assert.True(await TableExistsInPostgresAsync("repeater_parent"));
        var parentCols = await GetTableColumnsAsync("repeater_parent");
        var parentNames = parentCols.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("parent_field", parentNames);

        // Child table exists with system + child_field + parent FK column.
        Assert.True(await TableExistsInPostgresAsync("repeater_child"));
        var childCols = await GetTableColumnsAsync("repeater_child");
        var childMap = childCols.ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
        Assert.Contains("child_field", childMap.Keys);
        Assert.True(childMap.ContainsKey("parent_repeater_parent_id"), "FK column missing");
        Assert.Equal("uuid", childMap["parent_repeater_parent_id"]);

        // Index on FK column exists (AC-3).
        Assert.True(await IndexExistsInPostgresAsync("idx_repeater_child_parent"),
            "Expected idx_repeater_child_parent in pg_indexes");
    }

    [Fact]
    public async Task ProvisionWithRepeater_ChildAlreadyExists_FkColumnAddedIdempotently()
    {
        // AC-2 idempotent: child table pre-provisioned via its own bind, then a parent
        // bind walks back into it and ADD COLUMN IF NOT EXISTS adds the FK without
        // duplication. A second parent retry must be a true no-op on the FK column.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var childMenuId = await CreateMenuViaApiAsync(token, "RepIdemChildMenu", 0);
        var parentMenuId = await CreateMenuViaApiAsync(token, "RepIdemParentMenu", 1);

        var childRoot = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "cf1", type = "TextInput", properties = new { fieldKey = "leaf" },
                  children = Array.Empty<object>() },
        }};
        var childVersion = await CreateAndPublishDesignerWithFieldsAsync(
            token, "repeater_idem_child", childRoot);

        // Pre-provision child table by binding a menu to it (no parent yet → no FK).
        using (var b1 = await PutBindingAsync(token, childMenuId, "repeater_idem_child", childVersion))
            Assert.Equal(HttpStatusCode.Accepted, b1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, childMenuId, TimeSpan.FromSeconds(10)));

        // Sanity: child exists but has NO parent FK column yet.
        var childColsBefore = await GetTableColumnsAsync("repeater_idem_child");
        Assert.DoesNotContain("parent_repeater_idem_parent_id",
            childColsBefore.Select(c => c.Name));

        // Now create the parent and bind it — its provisioner walks into the child
        // and adds the FK column (ALTER TABLE ADD COLUMN IF NOT EXISTS).
        var parentRoot = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "pr1", type = "Repeater", properties = new { rowDesignerId = "repeater_idem_child", fieldKey = "child_rows" },
                  children = Array.Empty<object>() },
        }};
        var parentVersion = await CreateAndPublishDesignerWithFieldsAsync(
            token, "repeater_idem_parent", parentRoot);

        using (var b2 = await PutBindingAsync(token, parentMenuId, "repeater_idem_parent", parentVersion))
            Assert.Equal(HttpStatusCode.Accepted, b2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, parentMenuId, TimeSpan.FromSeconds(10)));

        var childColsAfter = await GetTableColumnsAsync("repeater_idem_child");
        Assert.Equal(1,
            childColsAfter.Count(c => c.Name == "parent_repeater_idem_parent_id"));
        var leafCount = childColsAfter.Count(c => c.Name == "leaf");
        Assert.Equal(1, leafCount);   // no duplicate user column either

        // Retry the parent bind — FK column add must be a no-op (IF NOT EXISTS).
        using (var retry = await PostRetryAsync(token, parentMenuId))
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, parentMenuId, TimeSpan.FromSeconds(10)));

        var childColsAfterRetry = await GetTableColumnsAsync("repeater_idem_child");
        Assert.Equal(1,
            childColsAfterRetry.Count(c => c.Name == "parent_repeater_idem_parent_id"));
    }

    [Fact]
    public async Task ProvisionWithRepeater_ThreeLevelNesting_AllTablesProvisioned()
    {
        // AC-5: A → B → C provisions all three tables in one transaction with FK + index
        // pointing at each level's direct parent.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "ThreeLevelMenu", 0);

        var rootC = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "cf1", type = "TextInput", properties = new { fieldKey = "leaf_value" },
                  children = Array.Empty<object>() },
        }};
        await CreateAndPublishDesignerWithFieldsAsync(token, "designer_c_l3", rootC);

        var rootB = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "bf1", type = "TextInput", properties = new { fieldKey = "mid_value" },
                  children = Array.Empty<object>() },
            new { id = "br1", type = "Repeater", properties = new { rowDesignerId = "designer_c_l3", fieldKey = "child_rows" },
                  children = Array.Empty<object>() },
        }};
        await CreateAndPublishDesignerWithFieldsAsync(token, "designer_b_l2", rootB);

        var rootA = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "af1", type = "TextInput", properties = new { fieldKey = "top_value" },
                  children = Array.Empty<object>() },
            new { id = "ar1", type = "Repeater", properties = new { rowDesignerId = "designer_b_l2", fieldKey = "child_rows" },
                  children = Array.Empty<object>() },
        }};
        var versionA = await CreateAndPublishDesignerWithFieldsAsync(token, "designer_a_l1", rootA);

        using (var bind = await PutBindingAsync(token, menuId, "designer_a_l1", versionA))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(15)));

        // All three tables exist.
        Assert.True(await TableExistsInPostgresAsync("designer_a_l1"));
        Assert.True(await TableExistsInPostgresAsync("designer_b_l2"));
        Assert.True(await TableExistsInPostgresAsync("designer_c_l3"));

        // B points at A; C points at B.
        var bCols = (await GetTableColumnsAsync("designer_b_l2"))
            .Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("parent_designer_a_l1_id", bCols);

        var cCols = (await GetTableColumnsAsync("designer_c_l3"))
            .Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("parent_designer_b_l2_id", cCols);

        // FK indexes exist at each level.
        Assert.True(await IndexExistsInPostgresAsync("idx_designer_b_l2_parent"));
        Assert.True(await IndexExistsInPostgresAsync("idx_designer_c_l3_parent"));
    }

    [Fact]
    public async Task BindDesigner_RepeaterCycle_Returns422WithRepeaterCycleCode()
    {
        // AC-4: cycle detector blocks the bind at request time. No DDL, no enqueued job.
        // Uses a genuine multi-node cycle A→B→A. (A direct self-edge A→A is an
        // intentional adjacency-list tree, not a cycle — see the self-reference test.)
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "CycleMenu", 0);

        // cycle_b → cycle_a, published first (the reference target need not exist yet).
        var rootB = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "rb", type = "Repeater", properties = new { rowDesignerId = "cycle_a", fieldKey = "a_rows" },
                  children = Array.Empty<object>() },
        }};
        await CreateAndPublishDesignerWithFieldsAsync(token, "cycle_b", rootB);

        // cycle_a → cycle_b closes the loop.
        var rootCycle = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "r1", type = "Repeater", properties = new { rowDesignerId = "cycle_b", fieldKey = "child_rows" },
                  children = Array.Empty<object>() },
        }};
        var publishedVersion = await CreateAndPublishDesignerWithFieldsAsync(
            token, "cycle_a", rootCycle);

        using var response = await PutBindingAsync(token, menuId, "cycle_a", publishedVersion);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("REPEATER_CYCLE", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("admin.menus.repeaterCycle", doc.RootElement.GetProperty("messageKey").GetString());

        // No table created — DDL never ran.
        Assert.False(await TableExistsInPostgresAsync("cycle_a"));

        // Menu's binding was never touched (returned before any state write).
        var menu = await GetMenuAsync(token, menuId);
        Assert.NotNull(menu);
        Assert.Null(menu!.DesignerId);
        Assert.Null(menu.BoundVersion);
        Assert.Null(menu.ProvisioningStatus);
    }

    [Fact]
    public async Task ProvisionSelfReferencingRepeater_CreatesOneTableWithSelfFkAndIndex()
    {
        // A component whose Repeater references ITSELF is an adjacency-list tree: a
        // single table with a parent_<table>_id self-FK back to its own id. The cycle
        // detector must allow it (a self-edge is not a cycle) and the provisioner must
        // emit the self-FK column + index without recursing.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "TreeMenu", 0);

        var treeRoot = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "tf1", type = "TextInput", properties = new { fieldKey = "node_name" },
                  children = Array.Empty<object>() },
            new { id = "tr1", type = "Repeater", properties = new { rowDesignerId = "tree_node", fieldKey = "children_rows" },
                  children = Array.Empty<object>() },
        }};
        var treeVersion = await CreateAndPublishDesignerWithFieldsAsync(
            token, "tree_node", treeRoot);

        using (var bind = await PutBindingAsync(token, menuId, "tree_node", treeVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // One table — system columns + node_name + a self-FK column referencing its own id.
        Assert.True(await TableExistsInPostgresAsync("tree_node"));
        var cols = await GetTableColumnsAsync("tree_node");
        var colMap = cols.ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
        Assert.Contains("node_name", colMap.Keys);
        Assert.True(colMap.ContainsKey("parent_tree_node_id"), "self-FK column missing");
        Assert.Equal("uuid", colMap["parent_tree_node_id"]);

        // Index on the self-FK column exists.
        Assert.True(await IndexExistsInPostgresAsync("idx_tree_node_parent"),
            "Expected idx_tree_node_parent in pg_indexes");
    }

    [Fact]
    public async Task ProvisionHostWithTreeView_AddsSelfFkAndIndexToNodeTemplateTable()
    {
        // A TreeView in a HOST designer references a SEPARATE node-template designer and
        // turns THAT table into a self-referencing adjacency-list tree: parent_<node>_id
        // REFERENCES <node>(id) + index. Unlike a Repeater it does NOT make the node table
        // a child of the host (no parent_<host>_id). The host gets one TEXT column for the
        // TreeView's own fieldKey (the view/select-mode selection).
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "TreeViewMenu", 0);

        // Node template — published, but not bound (host provisioning creates its table).
        var nodeRoot = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "nf1", type = "TextInput", properties = new { fieldKey = "node_label" },
                  children = Array.Empty<object>() },
        }};
        await CreateAndPublishDesignerWithFieldsAsync(token, "tv_node_tpl", nodeRoot);

        // Host designer carrying a TreeView that references the node template.
        var hostRoot = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "tv1", type = "TreeView",
                  properties = new { fieldKey = "selected_units", rowDesignerId = "tv_node_tpl" },
                  children = Array.Empty<object>() },
        }};
        var hostVersion = await CreateAndPublishDesignerWithFieldsAsync(token, "tv_host", hostRoot);

        using (var bind = await PutBindingAsync(token, menuId, "tv_host", hostVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // Host table carries the TreeView's own TEXT column (the selection), and is NOT a
        // tree itself (no parent_tv_host_id).
        Assert.True(await TableExistsInPostgresAsync("tv_host"));
        var hostCols = (await GetTableColumnsAsync("tv_host"))
            .ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
        Assert.Contains("selected_units", hostCols.Keys);
        Assert.False(hostCols.ContainsKey("parent_tv_host_id"));

        // Node-template table exists with the self-FK + index.
        Assert.True(await TableExistsInPostgresAsync("tv_node_tpl"));
        var nodeCols = (await GetTableColumnsAsync("tv_node_tpl"))
            .ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
        Assert.Contains("node_label", nodeCols.Keys);
        Assert.True(nodeCols.ContainsKey("parent_tv_node_tpl_id"), "self-FK column missing");
        Assert.Equal("uuid", nodeCols["parent_tv_node_tpl_id"]);
        Assert.True(await IndexExistsInPostgresAsync("idx_tv_node_tpl_parent"),
            "Expected idx_tv_node_tpl_parent in pg_indexes");
    }

    [Fact]
    public async Task ProvisionWithRepeater_ChildNotPublished_ParentProvisoningSucceeds()
    {
        // AC-1 edge case: parent Repeater references a designerId that has no Published
        // version. The provisioner skips it (per Task 1 step 5) and parent still succeeds.
        // No table is created for the unpublished child.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "UnpublishedChildMenu", 0);

        var parentRoot = new { id = "root", type = "Stack", properties = new { }, children = new object[]
        {
            new { id = "pf1", type = "TextInput", properties = new { fieldKey = "parent_only" },
                  children = Array.Empty<object>() },
            new { id = "pr1", type = "Repeater", properties = new { rowDesignerId = "unpublished_child", fieldKey = "child_rows" },
                  children = Array.Empty<object>() },
        }};
        var parentVersion = await CreateAndPublishDesignerWithFieldsAsync(
            token, "no_child_pub_parent", parentRoot);

        // Seed "unpublished_child" with a Draft-only version so the provisioner's
        // WHERE Status = "Published" filter returns null, exercising the skip-unpublished
        // code path (as opposed to the designer not existing at all).
        await CreateDesignerViaApiAsync(token, "unpublished_child");
        // v1 is left in Draft — no PutVersionStatusAsync call.

        using (var bind = await PutBindingAsync(token, menuId, "no_child_pub_parent", parentVersion))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        Assert.True(await TableExistsInPostgresAsync("no_child_pub_parent"));
        Assert.False(await TableExistsInPostgresAsync("unpublished_child"));
    }

    // ---------- Story 5.6 — admin schema drift view ----------

    [Fact]
    public async Task GetDrift_NoOrphanedColumns_ReturnsEmpty()
    {
        // AC-1: when DB schema matches the current Published version, no orphans.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DriftEmptyMenu", 0);

        var root = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "name_col" }, children = Array.Empty<object>() },
            },
        };
        var v = await CreateAndPublishDesignerWithFieldsAsync(token, "drift_empty", root);
        using (var bind = await PutBindingAsync(token, menuId, "drift_empty", v))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await GetDriftAsync(token, "drift_empty");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SchemaDriftResponseDto>();
        Assert.NotNull(body);
        Assert.Empty(body!.OrphanedColumns);
    }

    [Fact]
    public async Task GetDrift_OrphanedColumns_ReturnsListWithCounts()
    {
        // AC-1: bind v1 with two fields, then bind a newer version with one field —
        // the omitted field is retained in the DB (Story 5.4) and surfaces as orphaned.
        // Non-null row count reflects rows that actually have data in that column.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DriftOrphansMenu", 0);

        var rootV1 = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "active_col" }, children = Array.Empty<object>() },
                new { id = "f2", type = "TextInput",
                      properties = new { fieldKey = "obsolete_col" }, children = Array.Empty<object>() },
            },
        };
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "drift_orphans", rootV1);
        using (var bind1 = await PutBindingAsync(token, menuId, "drift_orphans", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // Insert 2 rows with non-null obsolete_col so EstimatedNonNullRowCount = 2.
        await InsertObsoleteRowsAsync("drift_orphans", "value-a", "value-b");

        // v3 removes obsolete_col.
        var rootV2 = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "active_col" }, children = Array.Empty<object>() },
            },
        };
        await PostVersionWithRootAsync(token, "drift_orphans", rootV2);
        await PutVersionStatusAsync(token, "drift_orphans", 3, "Published");
        using (var bind2 = await PutBindingAsync(token, menuId, "drift_orphans", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await GetDriftAsync(token, "drift_orphans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SchemaDriftResponseDto>();
        Assert.NotNull(body);
        var orphan = Assert.Single(body!.OrphanedColumns);
        Assert.Equal("obsolete_col", orphan.ColumnName);
        Assert.Equal("text", orphan.PgDataType);
        Assert.Equal(2, orphan.EstimatedNonNullRowCount);
    }

    [Fact]
    public async Task GetDrift_TableNotProvisioned_ReturnsEmpty()
    {
        // AC-1: when the table has not been provisioned yet, drift returns an empty
        // list (200), not a 404. The designer must exist as a row in component_schemas;
        // only the dynamic table is absent.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateDesignerViaApiAsync(token, "drift_no_table");

        using var response = await GetDriftAsync(token, "drift_no_table");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SchemaDriftResponseDto>();
        Assert.NotNull(body);
        Assert.Empty(body!.OrphanedColumns);
    }

    [Fact]
    public async Task GetDrift_SystemColumnsExcluded_NeverInList()
    {
        // AC-1: id/created_at/etc. are filtered out even though they're "extra"
        // columns from a pure information_schema perspective. They're invariants
        // of the provisioning pipeline, not user-authored fields.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DriftSysColsMenu", 0);
        await CreateAndPublishDesignerAsync(token, "drift_sys_cols");
        using (var bind = await PutBindingAsync(token, menuId, "drift_sys_cols", 1))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await GetDriftAsync(token, "drift_sys_cols");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SchemaDriftResponseDto>();
        Assert.NotNull(body);
        Assert.Empty(body!.OrphanedColumns);
        var sysCols = new[] { "id", "created_at", "created_by", "updated_at", "updated_by", "is_deleted", "cascade_event_id" };
        foreach (var s in sysCols)
            Assert.DoesNotContain(body.OrphanedColumns, c => c.ColumnName == s);
    }

    [Fact]
    public async Task DropColumn_OrphanedColumn_Returns204AndAuditsAndInvalidatesRegistry()
    {
        // AC-3 happy path: orphaned column drops, audit row written, registry purged.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DropOrphanMenu", 0);

        var rootV1 = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "active_col" }, children = Array.Empty<object>() },
                new { id = "f2", type = "TextInput",
                      properties = new { fieldKey = "obsolete_col" }, children = Array.Empty<object>() },
            },
        };
        var v1 = await CreateAndPublishDesignerWithFieldsAsync(token, "drop_orphan", rootV1);
        using (var bind1 = await PutBindingAsync(token, menuId, "drop_orphan", v1))
            Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var rootV2 = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "active_col" }, children = Array.Empty<object>() },
            },
        };
        await PostVersionWithRootAsync(token, "drop_orphan", rootV2);
        await PutVersionStatusAsync(token, "drop_orphan", 3, "Published");
        using (var bind2 = await PutBindingAsync(token, menuId, "drop_orphan", 3))
            Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        // Confirm both registry entries (v1 = 2, v2 = 3) are populated before drop.
        using (var preScope = _factory!.Services.CreateScope())
        {
            var registry = preScope.ServiceProvider.GetRequiredService<ISchemaRegistry>();
            Assert.NotNull(registry.TryGet("drop_orphan", v1));
            Assert.NotNull(registry.TryGet("drop_orphan", 3));
        }

        using var response = await DeleteColumnAsync(token, "drop_orphan", "obsolete_col");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Column gone from information_schema.
        var colsAfter = await GetTableColumnsAsync("drop_orphan");
        Assert.DoesNotContain(colsAfter, c => c.Name == "obsolete_col");

        // Audit row appended with ddl_operation = DROP and columns_dropped containing the col.
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ddl_operation, from_version, to_version, columns_dropped, actor_id, correlation_id
                FROM schema_audit_log
                WHERE designer_id = 'drop_orphan' AND ddl_operation = 'DROP'
                ORDER BY created_at DESC
                LIMIT 1
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Expected a DROP row in schema_audit_log");
            Assert.Equal("DROP", reader.GetString(0));
            Assert.True(await reader.IsDBNullAsync(1));   // from_version NULL for DROP
            Assert.Equal(0, reader.GetInt32(2));          // to_version = 0 sentinel
            var dropped = (string[]?)reader.GetValue(3);
            Assert.NotNull(dropped);
            Assert.Contains("obsolete_col", dropped!);
            Assert.False(await reader.IsDBNullAsync(4), "actor_id must be non-null on authenticated DROP");
            Assert.Equal(26, reader.GetString(5).Length); // ULID
        }
        finally
        {
            await conn.DisposeAsync();
        }

        // AC-4: registry invalidated for ALL versions of the designer.
        using var postScope = _factory!.Services.CreateScope();
        var registryAfter = postScope.ServiceProvider.GetRequiredService<ISchemaRegistry>();
        Assert.Null(registryAfter.TryGet("drop_orphan", v1));
        Assert.Null(registryAfter.TryGet("drop_orphan", 3));
    }

    [Fact]
    public async Task DropColumn_SystemColumn_Returns422()
    {
        // AC-6: dropping a system column is rejected with COLUMN_NOT_ORPHANED.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DropSysMenu", 0);
        await CreateAndPublishDesignerAsync(token, "drop_sys");
        using (var bind = await PutBindingAsync(token, menuId, "drop_sys", 1))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await DeleteColumnAsync(token, "drop_sys", "id");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("COLUMN_NOT_ORPHANED", doc.RootElement.GetProperty("code").GetString());

        // Column still present.
        var cols = await GetTableColumnsAsync("drop_sys");
        Assert.Contains(cols, c => c.Name == "id");
    }

    [Fact]
    public async Task DropColumn_FkColumn_Returns422()
    {
        // AC-6: FK-shaped column names (parent_*_id) are rejected with COLUMN_NOT_ORPHANED.
        // The IsFkColumn guard fires before any DB existence check, so no actual FK column
        // needs to be present in the table for this test.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DropFkMenu", 0);
        await CreateAndPublishDesignerAsync(token, "drop_fk");
        using (var bind = await PutBindingAsync(token, menuId, "drop_fk", 1))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await DeleteColumnAsync(token, "drop_fk", "parent_foo_id");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("COLUMN_NOT_ORPHANED", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DropColumn_ActiveColumn_Returns422()
    {
        // AC-6: a column that IS in the current Published schema is rejected.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DropActiveMenu", 0);

        var root = new
        {
            id = "root", type = "Stack", properties = new { }, children = new object[]
            {
                new { id = "f1", type = "TextInput",
                      properties = new { fieldKey = "still_active" }, children = Array.Empty<object>() },
            },
        };
        var v = await CreateAndPublishDesignerWithFieldsAsync(token, "drop_active", root);
        using (var bind = await PutBindingAsync(token, menuId, "drop_active", v))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await DeleteColumnAsync(token, "drop_active", "still_active");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("COLUMN_NOT_ORPHANED", doc.RootElement.GetProperty("code").GetString());

        // Column still present.
        var cols = await GetTableColumnsAsync("drop_active");
        Assert.Contains(cols, c => c.Name == "still_active");
    }

    [Fact]
    public async Task DropColumn_UnknownColumn_Returns404()
    {
        // Column does not exist on the live table → 404, not 422.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DropUnknownMenu", 0);
        await CreateAndPublishDesignerAsync(token, "drop_unknown");
        using (var bind = await PutBindingAsync(token, menuId, "drop_unknown", 1))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        using var response = await DeleteColumnAsync(token, "drop_unknown", "nonexistent_col");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("COLUMN_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Story 5.6 — deferred 5.2 binding-diff guard ----------

    [Fact]
    public async Task GetBindingDiff_TargetVersionZero_Returns422()
    {
        // AC-7: handler rejects targetVersion = 0 before calling the diff service.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DiffZeroMenu", 0);

        using var response = await GetBindingDiffAsync(token, menuId, targetVersion: 0);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_TARGET_VERSION", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetBindingDiff_TargetVersionNegative_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "DiffNegMenu", 0);

        using var response = await GetBindingDiffAsync(token, menuId, targetVersion: -1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("INVALID_TARGET_VERSION", doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- Unique-constraint picker — menu-less provisioned tables ----------

    [Fact]
    public async Task ProvisionedDesigners_MenulessProvisionedTable_AppearsInPickerAsync()
    {
        // Regression: a CRUD designer provisioned directly from the Table Provisioning
        // tab (no menu binding) must still appear in the Unique Constraint picker.
        // The picker previously JOINed through Menus and required a successful menu
        // binding, so menu-less provisioned tables were silently missing even though
        // their physical table exists.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateAndPublishDesignerAsync(token, "menuless_picker");

        // Provision directly via the Table Provisioning tab — no menu involved.
        using (var provision = await PostProvisionTableAsync(token, "menuless_picker", 1))
            Assert.Equal(HttpStatusCode.Accepted, provision.StatusCode);
        Assert.True(await PollUntilTableExistsAsync("menuless_picker", TimeSpan.FromSeconds(10)),
            "menu-less table was never provisioned");

        var picker = await GetProvisionedDesignersAsync(token);
        var item = Assert.Single(picker.Designers, d => d.DesignerId == "menuless_picker");
        Assert.Empty(item.MenuNames);    // menu-less → no menu decoration
        Assert.Null(item.BoundVersion);  // no binding, so no bound version
    }

    [Fact]
    public async Task ProvisionedDesigners_MenuBoundProvisionedTable_AppearsWithMenuNameAsync()
    {
        // Guards the decoration path: a menu-bound provisioned table still appears
        // and carries its menu name + bound version in the picker label.
        var token = await LoginAsync("admin@example.com", "Password1!");
        var menuId = await CreateMenuViaApiAsync(token, "PickerBoundMenu", 0);
        await CreateAndPublishDesignerAsync(token, "menubound_picker");
        using (var bind = await PutBindingAsync(token, menuId, "menubound_picker", 1))
            Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
        Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

        var picker = await GetProvisionedDesignersAsync(token);
        var item = Assert.Single(picker.Designers, d => d.DesignerId == "menubound_picker");
        Assert.Contains("PickerBoundMenu", item.MenuNames);
        Assert.Equal(1, item.BoundVersion);
    }

    [Fact]
    public async Task ProvisionedDesigners_DesignerWithoutTable_NotInPickerAsync()
    {
        // Locks the existence gate: a CRUD designer with no physical table (never
        // bound, never provisioned) must NOT appear — "provisioned" means the table
        // actually exists, mirroring the Table Provisioning tab's derived status.
        var token = await LoginAsync("admin@example.com", "Password1!");
        await CreateAndPublishDesignerAsync(token, "never_provisioned");

        var picker = await GetProvisionedDesignersAsync(token);
        Assert.DoesNotContain(picker.Designers, d => d.DesignerId == "never_provisioned");
    }

    // ---------- Helpers ----------

    // Story 5.5 — query pg_indexes for a non-unique FK index by name.
    private async Task<bool> IndexExistsInPostgresAsync(string indexName)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(1) > 0
                FROM pg_indexes
                WHERE schemaname = 'public' AND indexname = @indexName
                """;
            cmd.Parameters.AddWithValue("indexName", indexName);
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    private async Task<Guid> CreateMenuViaApiAsync(string token, string name, int order)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/menus")
        {
            Content = JsonContent.Create(new { name, order }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MenuResponseDto>();
        return body!.Id;
    }

    private async Task CreateDesignerViaApiAsync(string token, string designerId, string mode = "CRUD")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/designers")
        {
            Content = JsonContent.Create(new { designerId, displayName = designerId, mode }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task PostVersionAsync(string token, string designerId)
    {
        var rootElement = new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = Array.Empty<object>(),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/designers/{designerId}/versions")
        {
            Content = JsonContent.Create(new { rootElement }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    // Story 5.4 — POST a new version with a caller-supplied rootElement. Mirrors the
    // raw-JSON assembly trick used in CreateAndPublishDesignerWithFieldsAsync so the
    // SaveVersionRequest binder sees a real JSON object instead of "{}" produced by
    // JsonContent.Create's polymorphic resolution for object members.
    private async Task PostVersionWithRootAsync<TRoot>(string token, string designerId, TRoot rootElement)
    {
        ArgumentNullException.ThrowIfNull(rootElement);
        var rootJson = JsonSerializer.Serialize(rootElement, WebJsonOptions);
        var bodyJson = $"{{\"rootElement\":{rootJson}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/designers/{designerId}/versions")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task PutVersionStatusAsync(string token, string designerId, int version, string status)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/designers/{designerId}/versions/{version}/status")
        {
            Content = JsonContent.Create(new { status }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task CreateAndPublishDesignerAsync(string token, string designerId, string mode = "CRUD")
    {
        await CreateDesignerViaApiAsync(token, designerId, mode);
        await PutVersionStatusAsync(token, designerId, 1, "Published");
    }

    // Story 5.3 — creates a designer (v1 with null rootElement), POSTs a new
    // version v2 carrying the caller-supplied rootElement, then publishes v2.
    // The version number returned is what tests must bind to. We assemble the
    // request body as raw JSON so the SaveVersionRequest binder sees an actual
    // JSON object rather than `{}` (which is what a generic-T anonymous wrapper
    // serialises to via JsonContent.Create's polymorphic resolution for object
    // members).
    //
    // Returns the published version number so callers can bind to it.
    private async Task<int> CreateAndPublishDesignerWithFieldsAsync<TRoot>(
        string token,
        string designerId,
        TRoot rootElement)
    {
        ArgumentNullException.ThrowIfNull(rootElement);
        await CreateDesignerViaApiAsync(token, designerId);   // creates v1 (null root)

        var rootJson = JsonSerializer.Serialize(rootElement, WebJsonOptions);
        var bodyJson = $"{{\"rootElement\":{rootJson}}}";
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/designers/{designerId}/versions")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();

        const int publishedVersion = 2;
        await PutVersionStatusAsync(token, designerId, publishedVersion, "Published");
        return publishedVersion;
    }

    // Story 5.3 — direct PG inspection (bypasses the app layer) so tests can assert
    // the dynamic-table shape produced by DdlEmitter.
    private async Task<bool> TableExistsInPostgresAsync(string tableName)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(1) > 0
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = @tableName
                """;
            cmd.Parameters.AddWithValue("tableName", tableName);
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private async Task<IReadOnlyList<(string Name, string DataType)>> GetTableColumnsAsync(string tableName)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT column_name, data_type
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @tableName
                ORDER BY ordinal_position
                """;
            cmd.Parameters.AddWithValue("tableName", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<(string Name, string DataType)>();
            while (await reader.ReadAsync())
                result.Add((reader.GetString(0), reader.GetString(1)));
            return result;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private async Task<HttpResponseMessage> PutBindingAsync(string token, Guid menuId, string designerId, int version)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/admin/menus/{menuId}/binding")
        {
            Content = JsonContent.Create(new { designerId, version }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostRetryAsync(string token, Guid menuId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/menus/{menuId}/binding/retry");
        // Body is empty; the endpoint has no validator and reads no payload.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetBindingDiffAsync(string token, Guid menuId, int targetVersion)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/admin/menus/{menuId}/binding-diff?targetVersion={targetVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    // Provision a CRUD designer's table directly via the Table Provisioning tab
    // (no menu binding). The background service creates the table asynchronously.
    private async Task<HttpResponseMessage> PostProvisionTableAsync(string token, string designerId, int version)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/table-provisioning/{designerId}/provision")
        {
            Content = JsonContent.Create(new { version }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    // GET the Unique Constraint picker list.
    private async Task<ProvisionedDesignersDto> GetProvisionedDesignersAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/designers/provisioned");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProvisionedDesignersDto>())!;
    }

    // Menu-less provisioning exposes no menu status to poll, so wait on the physical
    // table appearing instead (the background job creates it asynchronously).
    private async Task<bool> PollUntilTableExistsAsync(string tableName, TimeSpan deadline)
    {
        var stop = DateTimeOffset.UtcNow.Add(deadline);
        do
        {
            if (await TableExistsInPostgresAsync(tableName)) return true;
            await Task.Delay(200);
        } while (DateTimeOffset.UtcNow < stop);
        return false;
    }

    // Story 5.6 — admin drift endpoints.
    private async Task<HttpResponseMessage> GetDriftAsync(string token, string designerId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/admin/designers/{designerId}/drift");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DeleteColumnAsync(string token, string designerId, string columnName)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/admin/designers/{designerId}/columns/{columnName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    // Inserts N rows into a provisioned table with non-null `obsolete_col` values.
    // Used by the drift count test to verify EstimatedNonNullRowCount accuracy.
    // tableName is a hardcoded test-fixture string ("drift_orphans"); the column
    // name is also hardcoded — no user input enters the SQL string.
#pragma warning disable CA2100 // tableName is a test-controlled constant, not user input
    private async Task InsertObsoleteRowsAsync(string tableName, params string[] values)
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        try
        {
            await conn.OpenAsync();
            foreach (var v in values)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"INSERT INTO {tableName} (obsolete_col) VALUES (@val)";
                cmd.Parameters.AddWithValue("val", v);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
#pragma warning restore CA2100

    private async Task<MenuResponseDto?> GetMenuAsync(string token, Guid menuId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/menus/{menuId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MenuResponseDto>();
    }

    // Polls the menu's provisioningStatus every 200 ms until it leaves "Pending"
    // (terminal Success or Error) or the deadline passes. Returns the terminal
    // status string. Mirrors the real admin SPA's polling pattern at a tighter
    // cadence so in-process integration tests don't add noticeable wall-time.
    private async Task<string?> PollUntilTerminalAsync(string token, Guid menuId, TimeSpan deadline)
    {
        var stop = DateTimeOffset.UtcNow.Add(deadline);
        string? status;
        do
        {
            var menu = await GetMenuAsync(token, menuId);
            status = menu?.ProvisioningStatus;
            if (status is not null and not "Pending") return status;
            await Task.Delay(200);
        } while (DateTimeOffset.UtcNow < stop);
        return status;
    }

    private static async Task ReseedSystemRolesAsync(FormForgeDbContext db)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == PlatformAdminRoleId))
        {
            db.Roles.Add(new Role
            {
                Id = PlatformAdminRoleId,
                Name = "platform-admin",
                IsSystem = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }
        if (!await db.Roles.AnyAsync(r => r.Id == ViewerRoleId))
        {
            db.Roles.Add(new Role
            {
                Id = ViewerRoleId,
                Name = "viewer",
                IsSystem = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedTestUsersAsync(FormForgeDbContext db)
    {
        var admin = new User
        {
            Email = "admin@example.com",
            DisplayName = "Platform Admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var viewer = new User
        {
            Email = "viewer@example.com",
            DisplayName = "Viewer User",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.AddRange(admin, viewer);
        await db.SaveChangesAsync();

        db.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = PlatformAdminRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.UserRoles.Add(new UserRole
        {
            UserId = viewer.Id,
            RoleId = ViewerRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MenuResponseDto(
        Guid Id,
        string Name,
        int Order,
        string? Icon,
        bool IsActive,
        Guid? ParentId,
        IReadOnlyList<Guid> AllowedRoleIds,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        string? DesignerId,
        int? BoundVersion,
        string? ProvisioningStatus,
        string? ProvisioningError);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record BindingDiffDto(
        BindingInfoDto? CurrentBinding,
        int TargetVersion,
        IReadOnlyList<string> ColumnsToAdd,
        IReadOnlyList<string> ColumnsAlreadyPresent,
        IReadOnlyList<string> OrphanedColumns,
        IReadOnlyList<string> WillTriggerChildProvisioning,
        IReadOnlyList<string> EstimatedDdl);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record BindingInfoDto(string DesignerId, int Version);

    // Story 5.6 — drift response DTOs for deserializing /api/admin/designers/{id}/drift.
    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record SchemaDriftResponseDto(IReadOnlyList<OrphanedColumnInfoDto> OrphanedColumns);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record OrphanedColumnInfoDto(
        string ColumnName,
        string PgDataType,
        long EstimatedNonNullRowCount);

    // Unique-constraint picker response (/api/admin/designers/provisioned).
    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record ProvisionedDesignersDto(IReadOnlyList<ProvisionedDesignerDto> Designers);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record ProvisionedDesignerDto(
        string DesignerId,
        string DisplayName,
        int? BoundVersion,
        IReadOnlyList<string> MenuNames);
}
