# Story 2.5: User-Role Assignment

Status: done

## Story

As a Platform Admin,
I want to assign and remove Roles from a User,
So that I can control their effective permissions.

## Acceptance Criteria

### AC-1 — `PUT /api/admin/users/{id}/roles` replaces a user's role set atomically

**Given** I am authenticated as a Platform Admin and user `{id}` exists
**When** I `PUT /api/admin/users/{id}/roles` with `{ "roleIds": ["<guid1>", "<guid2>"] }`
**Then** the response is HTTP 204 No Content
**And** the user's `user_roles` rows are replaced atomically — previous rows deleted, new rows inserted in a single `SaveChangesAsync`
**And** a user may hold zero roles (empty `roleIds: []` is valid)

**Given** I am authenticated as a Platform Admin and user `{id}` exists
**When** I `PUT /api/admin/users/{id}/roles` with `{ "roleIds": [] }`
**Then** the response is HTTP 204 No Content
**And** all `user_roles` rows for that user are deleted

### AC-2 — User not found returns 404

**Given** user `{id}` does not exist in the `users` table
**When** I `PUT /api/admin/users/{id}/roles`
**Then** the response is HTTP 404 with `ProblemDetails { code: "USER_NOT_FOUND", messageKey: "users.notFound" }`

### AC-3 — Invalid roleId returns 422

**Given** the `roleIds` array contains one or more UUIDs that do not exist in the `roles` table
**When** I `PUT /api/admin/users/{id}/roles`
**Then** the response is HTTP 422 with `ProblemDetails { code: "ROLES_NOT_FOUND", messageKey: "users.rolesNotFound", invalidRoleIds: [...] }`

### AC-4 — Validation: null or duplicate roleIds

**Given** the request body omits `roleIds` (null)
**When** validation runs
**Then** the response is HTTP 422 with a `ValidationProblemDetails` body

**Given** the `roleIds` array contains duplicate UUID entries
**When** validation runs
**Then** the response is HTTP 422 with `"Duplicate roleId values are not allowed."`

### AC-5 — Admin endpoints require `platform-admin` role and rate limiting

**Given** the request carries no JWT or an invalid JWT
**When** `PUT /api/admin/users/{id}/roles` is requested
**Then** the response is HTTP 401

**Given** the JWT identifies a user without the `platform-admin` role
**When** `PUT /api/admin/users/{id}/roles` is requested
**Then** the response is HTTP 403

### AC-6 — New login JWT reflects the updated role set

**Given** a user's roles have been replaced via the assignment endpoint
**When** that user calls `POST /api/auth/login` (or `POST /api/auth/refresh`)
**Then** the issued JWT `roles` claim reflects the new role set
**And** the user can (or cannot) access role-gated endpoints based on the updated claim

---

## Tasks / Subtasks

> All conventions from Stories 2.1–2.4 remain in force: `internal sealed` on new types, `CA1812 [SuppressMessage]` on DI-injected classes, `ArgumentNullException.ThrowIfNull` on handler/service parameters, `InvariantGlobalization=true` (`StringComparison.Ordinal`, `.ToLowerInvariant()`), `TreatWarningsAsErrors=true`, CPM (`Directory.Packages.props` only), `[LoggerMessage]` source-gen for any `ILogger` calls, single feature commit.

- [x] Task 1 — DTO: `AssignRolesRequest`
- [x] Task 2 — Validator: `AssignRolesRequestValidator`
- [x] Task 3 — `IUserService` / `UserService` with `AssignRolesAsync`
- [x] Task 4 — `UserEndpoints` with `MapUserAdminEndpoints` extension
- [x] Task 5 — Update `AdminEndpoints` to wire `/users` sub-group
- [x] Task 6 — Update `Program.cs`: register services and usings
- [x] Task 7 — Integration tests in `UserRoleIntegrationTests.cs`
- [x] Task 8 — Build (0 warn, 0 err), `dotnet format --verify-no-changes` clean, all tests green

---

### Task 1 — DTO: `AssignRolesRequest`

Create `src/FormForge.Api/Features/Users/Dtos/AssignRolesRequest.cs`:

```csharp
namespace FormForge.Api.Features.Users.Dtos;

internal sealed record AssignRolesRequest(IReadOnlyList<Guid>? RoleIds);
```

> **Why nullable `IReadOnlyList<Guid>?`:** Omitting the `roleIds` key in the JSON body yields `null` for a positional record. The validator's `NotNull()` rule returns a structured 422 instead of an NRE or unhandled exception. Same pattern as `CreateRoleRequest.Permissions` fix in Story 2.4 review.

---

### Task 2 — Validator: `AssignRolesRequestValidator`

Create `src/FormForge.Api/Features/Users/Validators/AssignRolesRequestValidator.cs`:

```csharp
using FluentValidation;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<AssignRolesRequest>.")]
internal sealed class AssignRolesRequestValidator : AbstractValidator<AssignRolesRequest>
{
    public AssignRolesRequestValidator()
    {
        RuleFor(x => x.RoleIds)
            .NotNull()
            .WithMessage("roleIds must be provided (use [] for empty).");

        RuleFor(x => x.RoleIds)
            .Must(ids => ids == null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Duplicate roleId values are not allowed.");
    }
}
```

> **Why not `RuleForEach`:** The rule here is about the collection as a whole (duplicate detection), not each element individually. A single `Must(...)` on the collection is cleaner and produces one error message rather than N.

---

### Task 3 — `IUserService` / `UserService`

Create `src/FormForge.Api/Features/Users/UserService.cs`:

```csharp
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Users;

internal enum AssignRolesOutcome { Success, UserNotFound, RolesNotFound }

internal sealed record AssignRolesResult(
    AssignRolesOutcome Outcome,
    IReadOnlyList<Guid>? InvalidRoleIds = null);

internal interface IUserService
{
    Task<AssignRolesResult> AssignRolesAsync(Guid userId, IReadOnlyList<Guid> roleIds, CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class UserService(FormForgeDbContext db) : IUserService
{
    public async Task<AssignRolesResult> AssignRolesAsync(
        Guid userId,
        IReadOnlyList<Guid> roleIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var userExists = await db.Users
            .AnyAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);

        if (!userExists)
        {
            return new AssignRolesResult(AssignRolesOutcome.UserNotFound);
        }

        var distinctRoleIds = roleIds.Distinct().ToList();

        if (distinctRoleIds.Count > 0)
        {
            var foundRoleIds = await db.Roles
                .Where(r => distinctRoleIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (foundRoleIds.Count != distinctRoleIds.Count)
            {
                var invalidIds = distinctRoleIds.Except(foundRoleIds).ToList();
                return new AssignRolesResult(AssignRolesOutcome.RolesNotFound, invalidIds);
            }
        }

        // Atomic replacement: load current rows, delete, insert new — single SaveChangesAsync.
        var existing = await db.UserRoles
            .Where(ur => ur.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.UserRoles.RemoveRange(existing);

        foreach (var roleId in distinctRoleIds)
        {
            db.UserRoles.Add(new UserRole
            {
                UserId = userId,
                RoleId = roleId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new AssignRolesResult(AssignRolesOutcome.Success);
    }
}
```

> **Why `RemoveRange` + `SaveChangesAsync` (not `ExecuteDeleteAsync`):** `RemoveRange` on tracked entities + a single `SaveChangesAsync` is one transaction — atomically deletes old rows and inserts new ones. `ExecuteDeleteAsync` executes immediately and separately from the subsequent `SaveChangesAsync`, requiring an explicit `BeginTransactionAsync` wrapper to stay atomic. `RemoveRange` is the simpler, safer choice here and matches the Story 2.4 pattern in `UpdateRoleAsync`.

> **Why `distinctRoleIds` via `Distinct()`:** The validator rejects duplicate UUIDs before the handler runs. `Distinct()` inside the service is a second-layer safeguard in case the service is called non-HTTP (e.g., tests seeding data). It is cheap and prevents a `user_roles` PK violation on `(UserId, RoleId)`.

> **Why check role existence before replacing:** The user's current role set must not be destroyed if the request references invalid roles. If we deleted first and then discovered invalid IDs, we'd have silently stripped the user of all roles on a bad request. Check validity first, then replace atomically.

> **No `UserRoleAssignmentChanged` event:** `IDomainEventBus` does not exist until Story 2.6. Story 2.5 does NOT publish events — same deferred pattern as Story 2.4's `RolePermissionsChanged` note. Story 2.6 will patch `AssignRolesAsync` to publish the event after `SaveChangesAsync`.

> **`IUserService` named for extensibility:** Story 2.8 (Admin User Management UI) adds `CreateUserAsync`, `DeactivateUserAsync`, etc. to the same `IUserService` / `UserService`. Naming it `IUserRoleService` now would require a rename or a confusing second service. Start with the canonical name.

---

### Task 4 — `UserEndpoints` with `MapUserAdminEndpoints`

Create `src/FormForge.Api/Features/Users/UserEndpoints.cs`:

```csharp
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users;

internal static class UserEndpoints
{
    // Registered in AdminEndpoints.MapAdminEndpoints as group.MapGroup("/users").
    // Future stories (2.7 /api/users/me, 2.8 user CRUD) extend this class.
    internal static RouteGroupBuilder MapUserAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPut("/{id:guid}/roles", AssignRolesHandler)
             .AddValidationFilter<AssignRolesRequest>()
             .WithSummary("Replace a user's complete role set atomically")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    private static async Task<IResult> AssignRolesHandler(
        Guid id,
        AssignRolesRequest request,
        IUserService userService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(userService);

        var result = await userService.AssignRolesAsync(id, request.RoleIds!, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            AssignRolesOutcome.UserNotFound => Results.Problem(
                title: "User not found",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "USER_NOT_FOUND", ["messageKey"] = "users.notFound" }),

            AssignRolesOutcome.RolesNotFound => Results.Problem(
                title: "One or more role IDs do not exist",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "ROLES_NOT_FOUND",
                    ["messageKey"] = "users.rolesNotFound",
                    ["invalidRoleIds"] = result.InvalidRoleIds,
                }),

            AssignRolesOutcome.Success => Results.NoContent(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
```

> **`request.RoleIds!` null-forgiving:** The validator guarantees `RoleIds` is non-null before the handler runs. The `!` suppresses the nullable warning without adding a dead-code null check.

---

### Task 5 — Update `AdminEndpoints`

Edit `src/FormForge.Api/Features/Roles/AdminEndpoints.cs`:

```csharp
using FormForge.Api.Features.Users;

namespace FormForge.Api.Features.Roles;

// Top-level admin dispatcher. Add MapXxxEndpoints() calls here rather than
// introducing parallel top-level /api/admin route groups in Program.cs.
internal static class AdminEndpoints
{
    internal static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGroup("/roles").WithTags("Admin — Roles").MapRoleEndpoints();
        group.MapGroup("/users").WithTags("Admin — Users").MapUserAdminEndpoints();
        return group;
    }
}
```

> **`using FormForge.Api.Features.Users;`** is required so the compiler resolves `MapUserAdminEndpoints` from `UserEndpoints.cs`. Add it as the first using in the file.

---

### Task 6 — Update `Program.cs`

**a) Add usings** at the top of `Program.cs` alongside the existing Roles usings:

```csharp
using FormForge.Api.Features.Users;
using FormForge.Api.Features.Users.Dtos;
using FormForge.Api.Features.Users.Validators;
```

**b) Register user services** after the Role services block (around line 100–102 of current `Program.cs`):

```csharp
// User services (Story 2.5)
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IValidator<AssignRolesRequest>, AssignRolesRequestValidator>();
```

> **`ValidationFilter<>` open-generic is already registered** at line 97 of `Program.cs` (`builder.Services.AddScoped(typeof(ValidationFilter<>))`). Do NOT add a second registration. The open-generic handles any `IValidator<T>` you register separately.

> **No route group changes to `Program.cs`:** The `/api/admin` group already calls `MapAdminEndpoints()`, which now includes `MapUserAdminEndpoints()` from Task 5. No additional `app.MapGroup(...)` call is needed.

---

### Task 7 — Integration tests

Create `src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Users;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class UserRoleIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = new("00000000-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    // IDs set in InitializeAsync so tests can reference them.
    private Guid _adminUserId;
    private Guid _viewerUserId;

    public UserRoleIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
            "TRUNCATE TABLE role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;");

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

    // ---------- Auth guard tests ----------

    [Fact]
    public async Task AssignRoles_Unauthenticated_Returns401()
    {
        using var response = await _client!.PutAsJsonAsync(
            new Uri($"/api/admin/users/{_viewerUserId}/roles", UriKind.Relative),
            new { roleIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssignRoles_AsNonAdmin_Returns403()
    {
        var token = await LoginAsync("viewer@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- 404 test ----------

    [Fact]
    public async Task AssignRoles_UserNotFound_Returns404()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{Guid.NewGuid()}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("USER_NOT_FOUND", body, StringComparison.Ordinal);
    }

    // ---------- Validation tests ----------

    [Fact]
    public async Task AssignRoles_NullRoleIds_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        // Omit roleIds entirely — positional record yields null.
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AssignRoles_DuplicateRoleIds_Returns422()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { ViewerRoleId, ViewerRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AssignRoles_UnknownRoleId_Returns422WithInvalidRoleIds()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");
        var fakeRoleId = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { ViewerRoleId, fakeRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ROLES_NOT_FOUND", body, StringComparison.Ordinal);
        Assert.Contains(fakeRoleId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Happy-path tests ----------

    [Fact]
    public async Task AssignRoles_ValidAssignment_Returns204()
    {
        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { ViewerRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var assignment = await db.UserRoles.FirstOrDefaultAsync(ur => ur.UserId == _viewerUserId && ur.RoleId == ViewerRoleId);
        Assert.NotNull(assignment);
    }

    [Fact]
    public async Task AssignRoles_EmptyRoleIds_Returns204AndClearsRoles()
    {
        // Pre-condition: give the viewer user a role.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.UserRoles.Add(new UserRole { UserId = _viewerUserId, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var token = await LoginAsync("admin@example.com", "Password1!");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = Array.Empty<Guid>() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var verifyScope = _factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        Assert.False(await verifyDb.UserRoles.AnyAsync(ur => ur.UserId == _viewerUserId));
    }

    [Fact]
    public async Task AssignRoles_JwtReflectsNewRoles_AfterLoginPostAssignment()
    {
        // Assign platform-admin role to the viewer user.
        var adminToken = await LoginAsync("admin@example.com", "Password1!");

        using var assignRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        };
        assignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var assignResponse = await _client!.SendAsync(assignRequest);
        Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);

        // Now log in as the viewer user — their fresh JWT should contain platform-admin.
        var viewerToken = await LoginAsync("viewer@example.com", "Password1!");

        // Functional proof: platform-admin role → can access admin endpoint.
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/roles");
        adminRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var adminResponse = await _client!.SendAsync(adminRequest);

        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task AssignRoles_RoleReplacement_IsAtomic()
    {
        // Start: viewer user has viewer role.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            db.UserRoles.Add(new UserRole { UserId = _viewerUserId, RoleId = ViewerRoleId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var token = await LoginAsync("admin@example.com", "Password1!");

        // Replace with platform-admin role only.
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{_viewerUserId}/roles")
        {
            Content = JsonContent.Create(new { roleIds = new[] { PlatformAdminRoleId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify: only platform-admin row remains; viewer row is gone.
        using var verifyScope = _factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var userRoles = await verifyDb.UserRoles.Where(ur => ur.UserId == _viewerUserId).ToListAsync();
        Assert.Single(userRoles);
        Assert.Equal(PlatformAdminRoleId, userRoles[0].RoleId);
    }

    // ---------- Helpers ----------

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

        // admin gets platform-admin; viewer starts with NO roles (tests assign via API).
        db.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = PlatformAdminRoleId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return (admin.Id, viewer.Id);
    }

    [SuppressMessage("Performance", "CA1812",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
}
```

---

### Task 8 — Build gates and verification

- [x] `dotnet build` — zero warnings, zero errors.
- [x] `dotnet format --verify-no-changes` — clean.
- [x] `dotnet test` — all 115 tests pass (10 new user-role integration tests + 105 pre-existing).
- [ ] `GET /openapi/v1.json` — includes `PUT /api/admin/users/{id}/roles` path with 204/404/422 response codes. _(manual review step)_
- [ ] Manual: `PUT /api/admin/users/{id}/roles` with a valid admin JWT and a non-existent userId → 404 with `USER_NOT_FOUND`. _(covered by `AssignRoles_UserNotFound_Returns404`)_
- [ ] Manual: `PUT /api/admin/users/{id}/roles` with a valid admin JWT and a non-existent roleId → 422 with `ROLES_NOT_FOUND`. _(covered by `AssignRoles_UnknownRoleId_Returns422WithInvalidRoleIds`)_
- [x] Functional equivalent of manual JWT-role check: `AssignRoles_JwtReflectsNewRoles_AfterLoginPostAssignment` proves a freshly-issued JWT grants the newly-assigned platform-admin role.

---

## Dev Notes

### What this story does — and what it does NOT

**In scope:**
1. `PUT /api/admin/users/{id}/roles` — atomic role-set replacement.
2. `IUserService` / `UserService` with `AssignRolesAsync`.
3. `AssignRolesRequest` DTO + `AssignRolesRequestValidator`.
4. `UserEndpoints.MapUserAdminEndpoints()` — `/users` sub-group in `AdminEndpoints`.
5. Integration tests (9 tests, Testcontainers, real Postgres).

**Out of scope (deferred to named stories):**
- Full user CRUD (create, deactivate, reactivate user) — Story 2.8.
- `UserRoleAssignmentChanged` domain event publishing — Story 2.6 (event bus doesn't exist yet).
- Permission cache eviction on assignment — Story 2.6 (`IMemoryCache` + `EffectivePermissionsCache` don't exist yet).
- `GET /api/users/me` and `GET /api/users/me/permissions` — Story 2.7.
- Frontend role-assignment UI (multi-select on user detail page) — Story 2.8.
- Self-deactivation guard — Story 2.8 (not relevant to role assignment).

### Architecture compliance

- **Decision 3.5 (Route groups):** `/api/admin/users` is a sub-group of `/api/admin`. `AdminEndpoints.MapAdminEndpoints` registers `group.MapGroup("/users").MapUserAdminEndpoints()`. No second `app.MapGroup(...)` in `Program.cs`.
- **AR-22 (Filter chain):** The admin group-level filters (`RequireAuth + RequirePlatformAdmin + RequireRateLimiting("admin")`) are inherited by `/api/admin/users/*`. No per-endpoint auth needed.
- **AR-18 (ProblemDetails):** All 4xx responses use `Results.Problem(...)` with `code` and `messageKey` in extensions. `correlationId` is injected globally by `AddProblemDetails`.
- **AR-47 (Domain events):** `UserRoleAssignmentChanged(userId)` should fire after a successful assignment per Decision 2.2. This event bus (`IDomainEventBus`) is Story 2.6 infrastructure. Story 2.5 leaves a known TODO: Story 2.6 will add event publishing to `AssignRolesAsync` after `SaveChangesAsync`. Do NOT stub a null bus or no-op interface now — that would be premature and require cleanup.
- **AR-45 (Naming):** New folder `Features/Users/` matches the architecture directory structure. `UserService`, `UserEndpoints`, `IUserService` follow PascalCase/`I`-prefix conventions.

### Current code state — files being modified

**`src/FormForge.Api/Features/Roles/AdminEndpoints.cs`** — Add `using FormForge.Api.Features.Users;` import and the `/users` sub-group registration inside `MapAdminEndpoints`. Do NOT change the `/roles` sub-group registration.

**`src/FormForge.Api/Program.cs`** — Add three usings and two `AddScoped` registrations. The `/api/admin` route group mapping in `Program.cs` is unchanged (it already calls `MapAdminEndpoints()` which now includes `/users`).

### New files

| File | Purpose |
|------|---------|
| `src/FormForge.Api/Features/Users/Dtos/AssignRolesRequest.cs` | Request DTO |
| `src/FormForge.Api/Features/Users/Validators/AssignRolesRequestValidator.cs` | FluentValidation |
| `src/FormForge.Api/Features/Users/UserService.cs` | `IUserService` + `UserService` |
| `src/FormForge.Api/Features/Users/UserEndpoints.cs` | `PUT /{id}/roles` handler + `MapUserAdminEndpoints` |
| `src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs` | 9 integration tests |

### Do NOT touch

- `src/FormForge.Api/Domain/Entities/UserRole.cs` — already correct from Story 2.4.
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — `UserRoles` DbSet is already registered.
- `src/FormForge.Api/Features/Auth/AuthService.cs` — already queries `db.UserRoles` for role names in both `LoginAsync` and `RefreshAsync`. No changes needed; AC-6 is satisfied by existing code.
- Any existing EF Core migration files — `user_roles` table already exists from Story 2.4.
- `web/src/**` — no frontend work in this story; UI is Story 2.8.
- `src/FormForge.Api/Features/Roles/RoleService.cs` — no changes.
- `src/FormForge.Api/Features/Roles/RoleEndpoints.cs` — no changes.

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Create a second top-level `app.MapGroup("/api/admin/users")` in `Program.cs` | The `/api/admin` group already calls `MapAdminEndpoints()`. Adding a parallel top-level group bypasses the `RequirePlatformAdmin + RequireRateLimiting("admin")` filters and duplicates the route prefix. Always add admin sub-features inside `MapAdminEndpoints`. |
| Delete all `user_roles` rows via `ExecuteDeleteAsync` then insert via `SaveChangesAsync` | `ExecuteDeleteAsync` runs immediately (two separate DB operations). Without an explicit `BeginTransactionAsync`, a crash between delete and insert leaves the user with no roles. Use `RemoveRange` + a single `SaveChangesAsync` for one atomic operation. |
| Publish `UserRoleAssignmentChanged` with a stubbed/null event bus | Story 2.6 introduces `IDomainEventBus`. Stubbing it now (null-guard, no-op interface, empty implementation) creates cleanup work and masks the missing wiring. Leave event publishing entirely for Story 2.6. |
| Rename the service `IUserRoleService` | The architecture specifies `UserService.cs`. Story 2.8 adds `CreateUserAsync`, `DeactivateUserAsync` to the same class. Starting with the canonical name avoids a rename refactor later. |
| Check `IsActive` on the target user before assigning roles | Assigning roles to inactive users is not blocked by spec. The AC says "user `{id}` exists" — just check existence. Story 2.8 handles deactivation semantics. |
| Return 400 for invalid roleIds | 422 Unprocessable Entity is correct — the request body references non-existent resources, making it semantically invalid. 400 is for malformed requests (wrong content-type, parse failure). |
| Load the existing `user_roles` rows after deleting | Load FIRST (to give `RemoveRange` tracked entities), then delete by saving with `RemoveRange`, then insert — all in one `SaveChangesAsync`. Never delete first then load to confirm deletion; that's a wasted roundtrip. |

### Previous story intelligence (Story 2.4 carry-forward)

- **TRUNCATE order in tests:** `TRUNCATE TABLE role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE` — already covers `user_roles`. The `UserRoleIntegrationTests` must use the same TRUNCATE statement and re-seed system roles via `ReseedSystemRolesAsync` (same helper as `RoleIntegrationTests`).
- **`HandleCookies = false`:** Still required in `WebApplicationFactoryClientOptions` for JWT-auth tests that use `Authorization: Bearer` headers manually.
- **`CA1812` suppression:** Required on `UserService` and `AssignRolesRequestValidator` (DI-injected classes).
- **`CA2000` suppression:** Required on the test class for `WebApplicationFactory` disposed via `DisposeAsync`.
- **`SuppressMessage` on deserialization DTOs:** Test-local record types used with `ReadFromJsonAsync` need the CA1812 suppression attribute.
- **System role seed is idempotent:** `ReseedSystemRolesAsync` guards with `AnyAsync` before adding — safe to call even if migrations already inserted the rows (TRUNCATE removes them, so it always re-inserts in tests).
- **`partial class Program` at end of `Program.cs`:** Already present. Do NOT remove — `WebApplicationFactory<Program>` depends on it.
- **`MapInboundClaims = false` on JWT bearer:** Already set in `Program.cs` from Story 2.4. The `roles` claim in the JWT resolves correctly to the user's assigned role names. No change needed.
- **Story 2.4 RoleIntegrationTests `DeleteRole_WithAssignments`:** Currently inserts user_roles directly via DbContext (workaround comment: "Story 2.5's user-role-assignment endpoint doesn't exist yet"). After Story 2.5 ships, that test COULD use the new endpoint but is not required to — the direct DbContext approach is still valid for test isolation.

### Git intelligence

Recent commits (most recent first):
- `832db1a` — Story 2.4 code review — apply 11 patches from three-reviewer pass
- `8a55795` — Story 2.4 — Role CRUD: /api/admin/roles CRUD + roles/role_permissions/user_roles schema + AuthService role query
- `f45a78c` — Story 2.3 code review — apply 2 patches from three-reviewer pass
- `6e4afa8` — Story 2.3 — Logout: POST /api/auth/logout + useLogoutMutation + AppLayout logout button

**Expected commit message for this story:**
`Story 2.5 — User-Role Assignment: PUT /api/admin/users/{id}/roles + UserService + integration tests`

### Testing approach

**Integration tests only (Testcontainers, real Postgres)** — same as Stories 2.1–2.4. Nine tests covering all ACs.

**No unit tests:** `UserService` is primarily DB interaction + validation. Validators are tested indirectly by the 422 assertions in integration tests.

**No frontend tests:** No frontend changes in this story.

**Test list and AC mapping:**

| Test | AC |
|---|---|
| `AssignRoles_Unauthenticated_Returns401` | AC-5 |
| `AssignRoles_AsNonAdmin_Returns403` | AC-5 |
| `AssignRoles_UserNotFound_Returns404` | AC-2 |
| `AssignRoles_NullRoleIds_Returns422` | AC-4 |
| `AssignRoles_DuplicateRoleIds_Returns422` | AC-4 |
| `AssignRoles_UnknownRoleId_Returns422WithInvalidRoleIds` | AC-3 |
| `AssignRoles_ValidAssignment_Returns204` | AC-1 |
| `AssignRoles_EmptyRoleIds_Returns204AndClearsRoles` | AC-1 |
| `AssignRoles_JwtReflectsNewRoles_AfterLoginPostAssignment` | AC-6 |
| `AssignRoles_RoleReplacement_IsAtomic` | AC-1 |

### References

- `_bmad-output/planning-artifacts/epics.md` — §"Story 2.5: User-Role Assignment"
- `_bmad-output/planning-artifacts/architecture.md`
  - Decision 2.2 — Permission cache + bust events (`UserRoleAssignmentChanged` — wired in Story 2.6)
  - AR-11 — Permission cache: `IMemoryCache` keyed by userId, 30s TTL, in-process event bus
  - AR-22 — Route group filter chain: correlation → auth → rate-limit → permission → validation → handler
  - AR-47 — Domain events: `IDomainEventBus`, `UserRoleAssignmentChanged(userId)`
  - AR-45 — Naming: `Features/Users/` folder, `UserService`, `UserEndpoints`, `IUserService`
- `_bmad-output/implementation-artifacts/2-4-role-crud.md` — carry-forward patterns (TRUNCATE order, HandleCookies, CA1812, CA2000, system role seed)
- `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` — the file being modified to add `/users` sub-group
- `src/FormForge.Api/Features/Auth/AuthService.cs` — already queries `db.UserRoles` in `LoginAsync` + `RefreshAsync`; AC-6 is pre-satisfied

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (1M context) — claude-opus-4-7[1m]

### Debug Log References

- Early API-project build after Tasks 1–6 (before tests written): 0 warnings, 0 errors.
- Test-project build after Task 7: 0 warnings, 0 errors.
- `dotnet format --verify-no-changes`: clean (exit 0).
- Full test suite: 115 passed, 0 failed, 0 skipped.
- Targeted `--filter "FullyQualifiedName~UserRoleIntegrationTests"`: 10/10 passed, ~17s wall (includes Testcontainers Postgres spin-up).

### Completion Notes List

- **Test count**: implementation has 10 user-role integration tests (matches the 10-row test mapping table in Dev Notes). The Task 8 line item in the story body said "9" — that was a Dev Notes drafting slip; the canonical list in `### Testing approach` has 10. Updated Task 8 to "10 new".
- **AC-6 verification approach**: rather than decoding the JWT and asserting on the `roles` claim payload (which would couple the test to claim names), `AssignRoles_JwtReflectsNewRoles_AfterLoginPostAssignment` does functional proof — after assigning `platform-admin` and re-logging in, the new token is accepted by the `RequirePlatformAdmin` policy on `/api/admin/roles`. Same level of confidence, less brittle.
- **Atomic-replacement pattern** matches Story 2.4 `UpdateRoleAsync` (`RemoveRange` + single `SaveChangesAsync`). Deliberately did NOT introduce `BeginTransactionAsync` since EF Core already wraps a single `SaveChangesAsync` in a transaction.
- **Story 2.6 deferral**: `UserRoleAssignmentChanged` event publishing is intentionally NOT wired (`IDomainEventBus` doesn't exist yet). Inline comment in `UserService.AssignRolesAsync` calls this out so 2.6 can patch it in directly.
- **No changes to**: `AuthService` (already reads `db.UserRoles` for role claims), `UserRole` entity, `FormForgeDbContext` (DbSet + key already configured by Story 2.4), migrations, frontend.
- **Validator pattern**: a single whole-collection `Must(...)` for duplicate detection rather than `RuleForEach`, because the rule is about the set, not each element — one error, not N.
- **`request.RoleIds!`** null-forgiving in the handler is safe because `ValidationFilter<AssignRolesRequest>` runs `NotNull()` before the handler ever executes.

### File List

**New files:**
- `src/FormForge.Api/Features/Users/Dtos/AssignRolesRequest.cs`
- `src/FormForge.Api/Features/Users/Validators/AssignRolesRequestValidator.cs`
- `src/FormForge.Api/Features/Users/UserService.cs`
- `src/FormForge.Api/Features/Users/UserEndpoints.cs`
- `src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs`

**Modified files:**
- `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` — add using + `/users` sub-group registration
- `src/FormForge.Api/Program.cs` — add 3 usings + 2 `AddScoped` registrations

### Change Log

- 2026-05-23 — Story 2.5 created (ready-for-dev). Story context engine analysis completed.
- 2026-05-23 — Story 2.5 implemented: `PUT /api/admin/users/{id}/roles` + `UserService.AssignRolesAsync` + `AssignRolesRequest` DTO + validator + 10 integration tests. Build 0/0, format clean, 115/115 tests pass. Status → review.
- 2026-05-23 — Code review (three-reviewer pass: Blind Hunter / Edge Case Hunter / Acceptance Auditor). All 6 ACs PASS. Triage: 2 decision-needed, 6 patches, 4 deferred, 10 dismissed as noise. See `### Review Findings`.
- 2026-05-23 — Review patches applied (8 total): 2 decisions resolved to Option A (catch+translate 409 for FK/PK races; last-admin lockout guard), 6 patches landed (negative-control assertion, hoist `now`, Guid.Empty validator, max-count validator, exact duplicate message assertion, removed unused `_adminUserId`). 4 deferred items recorded in `deferred-work.md`. Build 0/0, format clean, 117/117 tests pass (115 prior + 2 new: `AssignRoles_GuidEmptyInRoleIds_Returns422`, `AssignRoles_LastPlatformAdmin_CannotDemoteSelf_Returns422`). Status → done.

---

### Review Findings

Three-reviewer adversarial pass — Blind Hunter (diff-only), Edge Case Hunter (diff + project), Acceptance Auditor (diff + spec). All 6 ACs PASS per Acceptance Auditor.

#### Decision-needed (2 — both resolved to Option A)

- [x] **[Review][Decision] Concurrency hardening — race conditions on user-role mutation** [`src/FormForge.Api/Features/Users/UserService.cs`] — resolved: Option A. Added `AssignRolesOutcome.Conflict` and a `try/catch (DbUpdateException ... PostgresException { SqlState: "23503" or "23505" })` block around `SaveChangesAsync` that returns 409. Translates TOCTOU user-deleted, role-deleted (Story 2.4 deferred entry), and concurrent-PUT PK-collision into a clean 409 instead of unhandled 500. Lost-update remains last-writer-wins (acceptable; consistent with Story 2.4 pattern).
- [x] **[Review][Decision] Self-demotion / last-admin-lockout guard** [`src/FormForge.Api/Features/Users/UserService.cs`] — resolved: Option A. Added `AssignRolesOutcome.LastAdminLockout` → 422 with code `LAST_ADMIN_LOCKOUT` / messageKey `users.lastAdminLockout`. Guard fires when the request would strip the user's `platform-admin` role and no OTHER user holds it. Spec defers `IsActive` semantics to Story 2.8, but lockout prevention via role-removal belongs here because role-removal IS the vector.

#### Patch (6 — applied)

- [x] [Review][Patch] **Negative-control assertion missing in `AssignRoles_JwtReflectsNewRoles_AfterLoginPostAssignment`** [`src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs`] — Blind Hunter Finding 1. Added a pre-assignment login as viewer + assertion that `/api/admin/roles` returns 403. Hardens AC-6 against a broken `RequirePlatformAdmin` policy.
- [x] [Review][Patch] **Hoist `var now = DateTimeOffset.UtcNow` out of the role-insert loop** [`src/FormForge.Api/Features/Users/UserService.cs`] — Blind Hunter Finding 11. All rows inserted in one transaction now share an identical `CreatedAt` timestamp.
- [x] [Review][Patch] **Reject `Guid.Empty` in `roleIds` at validator** [`src/FormForge.Api/Features/Users/Validators/AssignRolesRequestValidator.cs`] — Edge Case Hunter F5. New rule `Must(ids => ids == null || ids.All(id => id != Guid.Empty))` with the message `"roleIds entries must not be Guid.Empty."`. Plus new test `AssignRoles_GuidEmptyInRoleIds_Returns422`.
- [x] [Review][Patch] **Cap `roleIds` collection length to prevent DoS** [`src/FormForge.Api/Features/Users/Validators/AssignRolesRequestValidator.cs`] — Edge Case Hunter F6. Constant `MaxRoleIds = 256` + new rule. Prevents pushing Postgres near its 65 535-parameter cap and bounds the O(N) `Distinct().Count()` cost.
- [x] [Review][Patch] **Strengthen duplicate-test to assert exact spec-mandated message** [`src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs`] — Acceptance Auditor A2. `Assert.Contains("Duplicate roleId values are not allowed.", body, StringComparison.Ordinal)` added to `AssignRoles_DuplicateRoleIds_Returns422`. Closes AC-4 coverage gap.
- [x] [Review][Patch] **Remove unused `_adminUserId` test field** [`src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs`] — Acceptance Auditor A4. Field removed; tuple destructure uses `_` discard.

#### Deferred (4)

- [x] [Review][Defer] **Token-revocation gap on role removal** [`src/FormForge.Api/Features/Users/UserService.cs`] — Edge Case Hunter F2 (High). Existing access tokens for the demoted user remain valid until expiry. Spec explicitly defers permission cache and `IDomainEventBus` to Story 2.6. Recorded in `deferred-work.md` with Story 2.6 as owner.
- [x] [Review][Defer] **Test fixture: migrations + BCrypt-12 hashes run per-test** [`src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs`] — Blind Hunter Finding 4. Pre-existing pattern from Stories 2.1–2.4; sweep belongs to a test-infrastructure story.
- [x] [Review][Defer] **TRUNCATE list duplicated across test classes — schema drift risk** [`src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs:53-54`] — Blind Hunter Finding 3. Same pattern as 2.4; extract to `PostgresFixture.ResetAsync()` in a future test-infra story.
- [x] [Review][Defer] **`InitializeAsync` factory-creation partial-failure not handled** [`src/FormForge.Api.Tests/Features/Users/UserRoleIntegrationTests.cs:38-63`] — Blind Hunter Finding 13. Pre-existing pattern from 2.1–2.4.

#### Dismissed (10)

- **`AssignRoles_RoleReplacement_IsAtomic` doesn't simulate mid-op failure** (Blind 2) — test verifies AC-1's end-state contract correctly; simulating mid-op rollback would require EF mocking.
- **`ReseedSystemRolesAsync` `if (!AnyAsync)` guard is dead code post-TRUNCATE** (Blind 5) — intentional idempotency guard.
- **Handler's `request.RoleIds!` null-forgiving fragility** (Blind 6 / Edge F8) — validator runs as endpoint filter before the handler; the `!` is correct.
- **`invalidRoleIds` echoed in 422 body could leak under loosened auth** (Blind 9) — endpoint is admin-only at the group level (`Program.cs:327-332`).
- **"No explicit `RequireAuthorization` on the new PUT"** (Blind 10) — false positive; parent admin group applies `RequireAuth().RequirePlatformAdmin().RequireRateLimiting("admin")`.
- **`ids.Distinct().Count() == ids.Count` double-enumeration** (Blind 12) — `IReadOnlyList<Guid>.Count` is O(1).
- **`Cors:AllowedOrigins:0` test setting** (Blind 14) — not a defect; mirrors production config shape.
- **No `IsActive` check on target user** (Edge F3) — spec explicitly lists this as an anti-pattern (Dev Notes anti-patterns table).
- **Comment block in `AdminEndpoints.cs` reworded vs spec** (Auditor A1) — cosmetic; both versions convey the same architectural intent.
- **Cosmetic blank-line after `using FormForge.Api.Features.Users;`** (Auditor A3) — formatter-driven; `dotnet format` clean.
