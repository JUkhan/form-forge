using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Audit;

// Story 6.8 — exercises GET /api/admin/data/{designerId}/audit end-to-end.
// Inserts mutation_audit_log rows directly via EF (no full CRUD pipeline) to
// isolate audit-endpoint behavior from the dynamic-CRUD pipeline. No
// [Collection] attribute — this class only touches static tables, so it can
// run independently of the DynamicCrudTests collection.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class MutationAuditLogIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private Guid _adminUserId;
    private Guid _viewerUserId;

    public MutationAuditLogIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE menu_role_assignments, menus, component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users, schema_audit_log, mutation_audit_log RESTART IDENTITY CASCADE;");

        // Drop any dynamically-provisioned tables left behind by sibling test classes.
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
                          'mutation_audit_log',
                          '__EFMigrationsHistory'
                      )
                LOOP
                    EXECUTE 'DROP TABLE IF EXISTS ' || quote_ident(r.tablename) || ' CASCADE';
                END LOOP;
            END $$;
            """);

        await ReseedSystemRolesAsync(db);
        (_adminUserId, _viewerUserId) = await SeedTestUsersAsync(db);

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

    // ---------- AC-4 ----------

    [Fact]
    public async Task GetMutationAuditLog_NoEntries_Returns200WithEmptyPage()
    {
        // AC-4: valid designerId with zero rows → 200 OK + empty page (NOT 404).
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, "never_mutated", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MutationAuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        Assert.Empty(body!.Data);
        Assert.Equal(0, body.Total);
        Assert.Equal(1, body.Page);
        Assert.Equal(25, body.PageSize);
        Assert.Equal(0, body.TotalPages);
    }

    // ---------- AC-1 ----------

    [Fact]
    public async Task GetMutationAuditLog_WithEntries_ReturnsEntriesNewestFirst()
    {
        // AC-1: two rows, ordering DESC, all fields populated.
        const string designerId = "test_designer";
        var olderRecordId = Guid.NewGuid();
        var newerRecordId = Guid.NewGuid();
        var older = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        await InsertMutationAuditRowAsync(_adminUserId, designerId, olderRecordId,
            operation: "CREATE", timestamp: older,
            newValues: """{"title":"old"}""");
        await InsertMutationAuditRowAsync(_adminUserId, designerId, newerRecordId,
            operation: "UPDATE", timestamp: newer,
            newValues: """{"title":"new"}""",
            previousValues: """{"title":"old"}""");

        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, designerId, page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MutationAuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        Assert.Equal(2, body!.Data.Count);
        Assert.Equal("UPDATE", body.Data[0].Operation);
        Assert.Equal(newerRecordId, body.Data[0].RecordId);
        Assert.Equal("CREATE", body.Data[1].Operation);
        Assert.Equal(olderRecordId, body.Data[1].RecordId);

        // Field-presence check on the newer entry.
        var first = body.Data[0];
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Equal(newer, first.Timestamp);
        Assert.Equal(_adminUserId, first.ActorId);
        Assert.Equal("Platform Admin", first.ActorName);
        Assert.Equal(designerId, first.DesignerId);
        // JSONB column round-trips with normalized whitespace; compare structurally.
        AssertJsonEqual("""{"title":"new"}""", first.NewValues);
        AssertJsonEqual("""{"title":"old"}""", first.PreviousValues);
        Assert.False(string.IsNullOrEmpty(first.CorrelationId));
    }

    // ---------- AC-5 ----------

    [Fact]
    public async Task GetMutationAuditLog_Paginated_SecondPage()
    {
        // AC-5: two rows, pageSize=1, page=2 → exactly the older row.
        const string designerId = "page_designer";
        var older = new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);

        await InsertMutationAuditRowAsync(_adminUserId, designerId, Guid.NewGuid(),
            operation: "CREATE", timestamp: older);
        await InsertMutationAuditRowAsync(_adminUserId, designerId, Guid.NewGuid(),
            operation: "UPDATE", timestamp: newer);

        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, designerId, page: 2, pageSize: 1);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MutationAuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        Assert.Single(body!.Data);
        Assert.Equal(2, body.Total);
        Assert.Equal(2, body.Page);
        Assert.Equal(1, body.PageSize);
        Assert.Equal(2, body.TotalPages);
        // Page 2 ordered DESC → oldest row.
        Assert.Equal("CREATE", body.Data[0].Operation);
    }

    // ---------- AC-1 (actor resolution) ----------

    [Fact]
    public async Task GetMutationAuditLog_ActorName_ResolvedFromUsers()
    {
        // AC-1: actorName resolved from users.DisplayName by actorId.
        const string designerId = "actor_designer";
        await InsertMutationAuditRowAsync(_adminUserId, designerId, Guid.NewGuid(),
            operation: "CREATE", timestamp: DateTimeOffset.UtcNow);

        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, designerId, page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MutationAuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        var entry = Assert.Single(body!.Data);
        Assert.Equal(_adminUserId, entry.ActorId);
        Assert.Equal("Platform Admin", entry.ActorName);
    }

    // ---------- AC-3 ----------

    [Fact]
    public async Task GetMutationAuditLog_InvalidDesignerId_Returns404()
    {
        // AC-3: uppercase + hyphen fails SafeIdentifier → 404 DESIGNER_NOT_FOUND.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, "INVALID-ID", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("DESIGNER_NOT_FOUND", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("admin.designers.notFound", doc.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- AC-2 ----------

    [Fact]
    public async Task GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405()
    {
        // AC-2: only GET is registered, so ASP.NET returns 405 for DELETE.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/admin/data/some_designer/audit");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---------- AC-6 ----------

    [Fact]
    public async Task GetMutationAuditLog_Viewer_Returns403()
    {
        // AC-6: viewer is authenticated but not platform-admin → 403.
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, "any_designer", page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- AC-5 (clamping) ----------

    [Fact]
    public async Task GetMutationAuditLog_PageBelowOne_ClampsToPage1()
    {
        // AC-5: page=0 → clamped to 1; response envelope must reflect the clamped value.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, "clamp_test_page", page: 0, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MutationAuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        Assert.Equal(1, body!.Page);
    }

    [Fact]
    public async Task GetMutationAuditLog_PageSizeAbove100_ClampsTo25()
    {
        // AC-5: pageSize=101 → clamped to 25; response envelope must reflect the clamped value.
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, "clamp_test_size", page: 1, pageSize: 101);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MutationAuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        Assert.Equal(25, body!.PageSize);
    }

    // ---------- AC-1 (null actorId / deleted actor) ----------

    [Fact]
    public async Task GetMutationAuditLog_NullActorId_ReturnsNullActorName()
    {
        // AC-1: actorId null → actorName null (no users lookup performed).
        const string designerId = "null_actor_designer";
        await InsertMutationAuditRowAsync(null, designerId, Guid.NewGuid(),
            operation: "CREATE", timestamp: DateTimeOffset.UtcNow);

        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, designerId, page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MutationAuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        var entry = Assert.Single(body!.Data);
        Assert.Null(entry.ActorId);
        Assert.Null(entry.ActorName);
    }

    [Fact]
    public async Task GetMutationAuditLog_DeletedActor_ReturnsNullActorName()
    {
        // AC-1: actorId present but user row absent from users table → actorName null (left-join fallback).
        const string designerId = "deleted_actor_designer";
        var orphanActorId = Guid.NewGuid(); // deliberately not seeded in users
        await InsertMutationAuditRowAsync(orphanActorId, designerId, Guid.NewGuid(),
            operation: "UPDATE", timestamp: DateTimeOffset.UtcNow);

        var token = await LoginAsync("admin@example.com", "Password1!");

        using var response = await GetMutationAuditAsync(token, designerId, page: 1, pageSize: 25);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MutationAuditPagedResultDto>(WebJsonOptions);
        Assert.NotNull(body);
        var entry = Assert.Single(body!.Data);
        Assert.Equal(orphanActorId, entry.ActorId);
        Assert.Null(entry.ActorName);
    }

    // ---------- Helpers ----------

    private static void AssertJsonEqual(string expected, string? actual)
    {
        Assert.NotNull(actual);
        using var ex = JsonDocument.Parse(expected);
        using var ac = JsonDocument.Parse(actual!);
        Assert.Equal(
            JsonSerializer.Serialize(ex, WebJsonOptions),
            JsonSerializer.Serialize(ac, WebJsonOptions));
    }

    private async Task InsertMutationAuditRowAsync(
        Guid? actorId,
        string designerId,
        Guid recordId,
        string operation,
        DateTimeOffset timestamp,
        string? newValues = null,
        string? previousValues = null)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.MutationAuditLog.Add(new MutationAuditLogEntry
        {
            DesignerId = designerId,
            RecordId = recordId,
            Operation = operation,
            ActorId = actorId,
            Timestamp = timestamp,
            NewValues = newValues,
            PreviousValues = previousValues,
            CorrelationId = Guid.NewGuid().ToString("N")[..26],
        });
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> GetMutationAuditAsync(
        string token, string designerId, int page, int pageSize)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/admin/data/{Uri.EscapeDataString(designerId)}/audit?page={page}&pageSize={pageSize}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await _client!.PostAsJsonAsync("/api/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        return body!.AccessToken;
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

    private static async Task<(Guid AdminId, Guid ViewerId)> SeedTestUsersAsync(FormForgeDbContext db)
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

        return (admin.Id, viewer.Id);
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MutationAuditPagedResultDto(
        IReadOnlyList<MutationAuditEntryDto> Data,
        long Total,
        int Page,
        int PageSize,
        int TotalPages);

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record MutationAuditEntryDto(
        Guid Id,
        DateTimeOffset Timestamp,
        Guid? ActorId,
        string? ActorName,
        string DesignerId,
        Guid RecordId,
        string Operation,
        string? PreviousValues,
        string? NewValues,
        string? CorrelationId);
}
