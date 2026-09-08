using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Domain.ValueTypes;
using FormForge.Api.Features.Datasets;
using FormForge.Api.Features.Provisioning;
using FormForge.Api.Features.Tenancy;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.Tenancy;

// Story 12.6 — end-to-end coverage for the shared tenant-schema-routing mechanism
// itself (TenantSchemaConnectionInterceptor + DbConnectionFactory/PreviewConnectionFactory
// reading ITenantContext at connection-open time), not any feature's business logic:
//   • EF and Dapper both resolve against the SAME tenant schema for the same request
//     scope, with no independent schema resolution — and neither can see the other
//     tenant's rows, nor `public`'s (I/O matrix rows 1 and 4/isolation).
//   • A platform-super-admin / unauthenticated request still targets `public`,
//     unchanged — proved via GET /api/admin/tenants (Story 12.5, EF-backed, public
//     schema) and the default (no ITenantContext.Set() call) search_path (I/O matrix
//     row 3).
//
// This deliberately drives the mechanism directly (a real DI scope + ITenantContext,
// exactly as TenantContextMiddleware would populate it for a real tenant request)
// rather than through a full Designer/DynamicCrud HTTP round trip. Two independent,
// pre-existing gaps make the full HTTP path unusable for a tenant-authenticated CRUD
// request today, NEITHER of which this story's Code Map lists or its Boundaries permit
// fixing here (each is its own future story's tenant-awareness work):
//   1. ProvisioningBackgroundService (the queue DdlEmitter jobs are processed on) is a
//      hosted service with its own DI scope that never passes through
//      TenantContextMiddleware, so its ITenantContext is always unset — a menu-less
//      "Table Provisioned" admin job silently targets `public` regardless of which
//      tenant enqueued it.
//   2. PermissionService (Singleton) resolves its own FormForgeDbContext via
//      IServiceScopeFactory.CreateScope() — a fresh scope with an unrelated, always-
//      unset ITenantContext — so GetCrudFlagsAsync/GetEffectivePermissionsAsync always
//      query `public.user_roles`/`public.users`, never the tenant's own schema. Every
//      RequirePermission("read"/"create"/...)-gated /api/data/* route therefore 403s
//      for every tenant user regardless of this story's changes.
// Real Testcontainers Postgres throughout — no mocks — same posture as
// TenantAwareLoginIntegrationTests/TenantProvisioningServiceTests.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class TenantSchemaRoutingIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string SuperAdminEmail = "routing.super@formforge.test";
    private const string SuperAdminPassword = "SuperSecret1!";

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public TenantSchemaRoutingIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE tenants, platform_admins, tenant_user_index, refresh_tokens, users, menus RESTART IDENTITY CASCADE;");

        db.PlatformAdmins.Add(new PlatformAdmin
        {
            UserEmail = SuperAdminEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(SuperAdminPassword, 12),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

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

    // Mirrors TenantAwareLoginIntegrationTests.ProvisionTenantAsync — real tenant row +
    // real provisioned schema (full static migration replay), marked Active.
    private async Task<Tenant> ProvisionTenantAsync(string schemaName)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var provisioningSvc = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var tenant = new Tenant { Name = $"Tenant {schemaName}", SchemaName = schemaName, Status = "Provisioning" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        await provisioningSvc.ProvisionSchemaAsync(tenant, CancellationToken.None);

        tenant.Status = "Active";
        await db.SaveChangesAsync();

        return tenant;
    }

    [Fact]
    public async Task EfAndDapper_ResolveToSameTenantSchema_WithFullIsolationAcrossTenantsAndPublic()
    {
        var tenantA = await ProvisionTenantAsync("tenant_route_ef_dapper_a");
        var tenantB = await ProvisionTenantAsync("tenant_route_ef_dapper_b");
        const string menuName = "route-probe-menu";

        // ---- Tenant A's own request scope: EF write, then Dapper read, same scope ----
        using (var scopeA = _factory!.Services.CreateScope())
        {
            var tenantContext = (TenantContext)scopeA.ServiceProvider.GetRequiredService<ITenantContext>();
            tenantContext.Set(tenantA.Id, tenantA.SchemaName);

            // EF write — FormForgeDbContext's connection is opened through
            // TenantSchemaConnectionInterceptor, which must set search_path to
            // tenantA.SchemaName for this scope's ITenantContext.
            var db = scopeA.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.Menus.Add(new Menu { Name = menuName, Order = 0, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            // Dapper read — DbConnectionFactory.CreateOpenConnectionAsync() reads the
            // SAME scope's ITenantContext inline. Both must agree on the schema: the
            // connection's own current_schema() must be tenantA's, and the row EF just
            // wrote must be visible to this fresh Dapper connection without any explicit
            // schema qualification in the SQL.
            var connectionFactory = scopeA.ServiceProvider.GetRequiredService<DbConnectionFactory>();
            await using var conn = await connectionFactory.CreateOpenConnectionAsync();

            var currentSchema = await conn.ExecuteScalarAsync<string>("SELECT current_schema()");
            Assert.Equal(tenantA.SchemaName, currentSchema);

            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM menus WHERE name = @menuName", new { menuName });
            Assert.Equal(1, count);
        }

        // ---- Tenant B's own request scope: neither EF nor Dapper sees tenant A's row ----
        using (var scopeB = _factory!.Services.CreateScope())
        {
            var tenantContext = (TenantContext)scopeB.ServiceProvider.GetRequiredService<ITenantContext>();
            tenantContext.Set(tenantB.Id, tenantB.SchemaName);

            var db = scopeB.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            var efVisible = await db.Menus.AsNoTracking().AnyAsync(m => m.Name == menuName);
            Assert.False(efVisible, "Tenant B's EF query must not see tenant A's row.");

            var connectionFactory = scopeB.ServiceProvider.GetRequiredService<DbConnectionFactory>();
            await using var conn = await connectionFactory.CreateOpenConnectionAsync();

            var currentSchema = await conn.ExecuteScalarAsync<string>("SELECT current_schema()");
            Assert.Equal(tenantB.SchemaName, currentSchema);

            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM menus WHERE name = @menuName", new { menuName });
            Assert.Equal(0, count);
        }

        // ---- No tenant resolved at all (platform-super-admin/anonymous default) ----
        using (var scopePublic = _factory!.Services.CreateScope())
        {
            var connectionFactory = scopePublic.ServiceProvider.GetRequiredService<DbConnectionFactory>();
            await using var conn = await connectionFactory.CreateOpenConnectionAsync();

            var currentSchema = await conn.ExecuteScalarAsync<string>("SELECT current_schema()");
            Assert.Equal("public", currentSchema);

            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM menus WHERE name = @menuName", new { menuName });
            Assert.Equal(0, count);
        }
    }

    // Review finding — none of the other tests here resolve DdlEmitter/
    // TableProvisioningService with a real (non-public) tenant schema set, so a
    // regression that hardcoded either back to "public" would go undetected (every
    // other exercise of their `@schema`-bound information_schema queries runs with
    // ITenantContext unset, where Schema resolves to "public" both before and after
    // this story). This drives both from the SAME tenant-scoped DI scope: DdlEmitter
    // creates the physical table (must land in tenantA's own schema), then
    // TableProvisioningService.ListAsync's GetExistingTableNamesAsync (a Dapper query
    // bound to TableProvisioningService.Schema) must see it as provisioned there.
    [Fact]
    public async Task DdlEmitterAndTableProvisioningService_ObserveTenantsOwnSchema_NotPublic()
    {
        var tenantA = await ProvisionTenantAsync("tenant_route_ddl_a");
        const string designerId = "route_ddl_probe";

        using var scope = _factory!.Services.CreateScope();
        var tenantContext = (TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.Set(tenantA.Id, tenantA.SchemaName);

        // Seed a minimal one-field CRUD designer directly into the tenant's own schema —
        // via FormForgeDbContext resolved from this same tenant-scoped DI container.
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.ComponentSchemas.Add(new ComponentSchema
        {
            DesignerId = designerId,
            DisplayName = designerId,
            Mode = "CRUD",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var rootElement = JsonSerializer.Serialize(new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "f1",
                    type = "TextInput",
                    properties = new { fieldKey = "title" },
                    children = Array.Empty<object>(),
                },
            },
        });
        db.ComponentSchemaVersions.Add(new ComponentSchemaVersion
        {
            DesignerId = designerId,
            Version = 1,
            Status = "Published",
            RootElement = rootElement,
            CreatedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // DdlEmitter, resolved from the SAME tenant-scoped DI container, must create
        // the physical table in tenantA's own schema — not public.
        var ddlEmitter = scope.ServiceProvider.GetRequiredService<DdlEmitter>();
        await ddlEmitter.EmitAsync(
            new ProvisioningJob(MenuId: null, designerId, Version: 1, ActorId: null),
            CancellationToken.None);

        // TableProvisioningService, resolved from the SAME scope: its Schema property
        // must resolve to tenantA's schema (not a hardcoded "public" literal), or the
        // Dapper existence check would miss the table DdlEmitter just created.
        var provisioningService = scope.ServiceProvider.GetRequiredService<TableProvisioningService>();
        var page = await provisioningService.ListAsync(1, 25, designerId, CancellationToken.None);
        var item = Assert.Single(page.Data);
        Assert.Equal(designerId, item.DesignerId);
        Assert.True(item.IsProvisioned,
            "TableProvisioningService must observe the table in the tenant's own schema, not public.");
    }

    // Review finding — no test exercised the tenant `{schema}_datasets` namespace;
    // every touched Dataset test only ran with ITenantContext unset (the legacy
    // `SchemaName == null → "datasets"` fallback). This provisions a tenant, creates
    // its `{schema}_datasets` namespace (the same CREATE SCHEMA TenantOnboardingService
    // issues during real onboarding), then drives DatasetViewManager (creates the VIEW)
    // and DatasetSourceResolver (reads it back) from a tenant-scoped DI scope —
    // confirming both resolve to "{schema}_datasets", never the legacy global
    // `datasets` schema.
    [Fact]
    public async Task DatasetViewManagerAndSourceResolver_UseTenantsOwnDatasetsNamespace_NotLegacyGlobal()
    {
        var tenantA = await ProvisionTenantAsync("tenant_route_view_a");
        var datasetsSchema = $"{tenantA.SchemaName}_datasets";

        var tenantCsb = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            SearchPath = tenantA.SchemaName,
        };
        await using (var setupConn = new NpgsqlConnection(tenantCsb.ConnectionString))
        {
            await setupConn.OpenAsync();
            await setupConn.ExecuteAsync($"CREATE SCHEMA \"{datasetsSchema}\"");
        }

        using var scope = _factory!.Services.CreateScope();
        var tenantContext = (TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.Set(tenantA.Id, tenantA.SchemaName);

        Assert.True(DatasetName.TryCreate("route_view_probe", out var name, out _));

        var connectionFactory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
        var viewManager = scope.ServiceProvider.GetRequiredService<DatasetViewManager>();

        await using (var conn = await connectionFactory.CreateOpenConnectionAsync())
        {
            var tx = await conn.BeginTransactionAsync(CancellationToken.None);
            try
            {
                await viewManager.CreateAsync(conn, tx, name!, "SELECT 1 AS n", CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);
            }
            finally
            {
                await tx.DisposeAsync();
            }
        }

        // The VIEW must physically exist in "{schema}_datasets" — never the legacy
        // global "datasets" schema.
        await using (var checkConn = await connectionFactory.CreateOpenConnectionAsync())
        {
            var foundInTenantSchema = await checkConn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM information_schema.views WHERE table_schema = @schema AND table_name = @name)",
                new { schema = datasetsSchema, name = name!.Value });
            Assert.True(foundInTenantSchema);

            var foundInLegacySchema = await checkConn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM information_schema.views WHERE table_schema = 'datasets' AND table_name = @name)",
                new { name = name!.Value });
            Assert.False(foundInLegacySchema);
        }

        // The custom_dataset row lives in the tenant's OWN schema (unqualified table
        // name, resolved via this scope's search_path) — what DatasetSourceResolver
        // reads to find the VIEW.
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.CustomDatasets.Add(new CustomDataset
        {
            DatasetName = name!.Value,
            IsCustomQuery = true,
            QueryType = DatasetQueryTypes.View,
            Query = "SELECT 1 AS n",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await using var resolveConn = await connectionFactory.CreateOpenConnectionAsync();
        var resolved = await DatasetSourceResolver.ResolveByNameAsync(
            resolveConn, name!, queryParametersJson: null, allowParameterized: false,
            timeout: 5, datasetsSchema: TenantDatasetSchemaResolver.Resolve(tenantContext),
            requireParameterValues: true, CancellationToken.None);

        Assert.Equal(DatasetSourceOutcome.Ok, resolved.Outcome);
        Assert.Equal($"\"{datasetsSchema}\".\"{name!.Value}\"", resolved.Source!.Relation);
    }

    [Fact]
    public async Task PlatformSuperAdminRequest_TargetsPublicSchema_Unaffected()
    {
        var superToken = await LoginAsync(SuperAdminEmail, SuperAdminPassword);
        var tenantA = await CreateTenantAsync(superToken, "Public Unaffected Co", "tenant_route_public_unaffected");

        // GET /api/admin/tenants is EF-backed (FormForgeDbContext.Tenants) and lives in
        // `public`. The platform-super-admin JWT never carries a tenantId claim, so
        // TenantContextMiddleware leaves ITenantContext unset for this request — the
        // query must still land on `public` and see the tenant row just created there,
        // exactly as it did before this story wired up any tenant-schema routing.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/tenants?page=1&pageSize=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", superToken);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResultDto<TenantDto>>();
        Assert.NotNull(body);
        Assert.Contains(body!.Data, t => t.Id == tenantA.Id && t.Status == "Active");

        // Unauthenticated request to the same group: still 401, unaffected.
        using var anonResponse = await _client!.GetAsync(new Uri("/api/admin/tenants", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);
    }

    // ---------- Helpers ----------

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
    }

    // Mirrors TenantEndpointsIntegrationTests' CreateTenant flow: POST /api/admin/tenants
    // provisions the schema (Story 12.2) and onboards the first admin (Story 12.7)
    // synchronously — by the time this returns, the tenant is Active.
    private async Task<TenantDto> CreateTenantAsync(string superToken, string name, string schemaName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Content = JsonContent.Create(new { name, schemaName }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", superToken);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CreateTenantResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Active", body!.Tenant.Status);

        return body.Tenant;
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string? RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record PagedResultDto<T>(IReadOnlyList<T> Data, long Total, int Page, int PageSize, int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record TenantDto(Guid Id, string Name, string SchemaName, string Status, DateTimeOffset CreatedAt);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record CreateTenantResponseDto(TenantDto Tenant, string TemporaryPassword);
}
