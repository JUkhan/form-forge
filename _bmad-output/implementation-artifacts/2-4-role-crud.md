# Story 2.4: Role CRUD

Status: done

<!-- Note: Validate with validate-create-story if needed before dev-story. -->

## Story

As a Platform Admin,
I want to create, edit, and delete Roles with per-Resource CRUD-flag configuration,
so that I can define what each Role is permitted to do on each data module.

## Acceptance Criteria

### AC-1 — System roles seeded at migration time

**Given** the API starts for the first time and `Database.Migrate()` runs
**When** the EF migration completes
**Then** two system roles exist in the `roles` table:
- `platform-admin` with `is_system = true` (full CRUD on all resources and admin areas — enforced by Story 2.6 via special-case logic, not per-resource rows)
- `viewer` with `is_system = true` (canRead only on all resources — enforced by Story 2.6 the same way)
**And** both rows have stable, deterministic UUIDs so re-running the migration is idempotent

### AC-2 — `GET /api/admin/roles` returns a paginated role list

**Given** I am authenticated as a Platform Admin (JWT includes role claim `platform-admin`)
**When** I `GET /api/admin/roles?page=1&pageSize=25`
**Then** the response is HTTP 200 with body `PagedResult<RoleListItem>` (per AR-21):
```json
{
  "data": [{ "id": "...", "name": "platform-admin", "description": null, "permissionCount": 0, "isSystem": true }],
  "total": 2,
  "page": 1,
  "pageSize": 25,
  "totalPages": 1
}
```
**And** results are ordered by `name` ascending

**Given** the request carries no JWT or an invalid JWT
**When** `GET /api/admin/roles` is requested
**Then** the response is HTTP 401

**Given** the JWT identifies a user without the `platform-admin` role
**When** `GET /api/admin/roles` is requested
**Then** the response is HTTP 403

### AC-3 — `POST /api/admin/roles` creates a role

**Given** I am authenticated as a Platform Admin
**When** I `POST /api/admin/roles` with a valid body:
```json
{
  "name": "content-editor",
  "description": "Can create and update records",
  "permissions": [
    { "resourceId": "incident_report", "canCreate": true, "canRead": true, "canUpdate": true, "canDelete": false }
  ]
}
```
**Then** the response is HTTP 201 with `Location: /api/admin/roles/{newId}` and body matching the full `RoleResponse`
**And** a corresponding `roles` row and any `role_permissions` rows are persisted

**Given** I POST with a `name` already used by an existing role (case-insensitive)
**When** `POST /api/admin/roles` is submitted
**Then** the response is HTTP 409 with `ProblemDetails { code: "ROLE_NAME_CONFLICT", messageKey: "roles.nameConflict", correlationId: "..." }`

**Given** I POST with an empty or missing `name`
**When** validation runs
**Then** the response is HTTP 422 with a `ValidationProblemDetails` body listing the field error

### AC-4 — `GET /api/admin/roles/{id}` returns a single role with its permissions

**Given** I am authenticated as a Platform Admin and role `{id}` exists
**When** I `GET /api/admin/roles/{id}`
**Then** the response is HTTP 200 with `RoleResponse` containing the role fields and its full `permissions` array

**Given** role `{id}` does not exist
**When** `GET /api/admin/roles/{id}` is requested
**Then** the response is HTTP 404 with `ProblemDetails { code: "ROLE_NOT_FOUND", messageKey: "roles.notFound" }`

### AC-5 — `PUT /api/admin/roles/{id}` updates a role

**Given** I am authenticated as a Platform Admin and the role is not a system role
**When** I `PUT /api/admin/roles/{id}` with `{ name, description, permissions: [...] }`
**Then** the response is HTTP 204 No Content
**And** the role's name and description are updated
**And** the `role_permissions` rows for this role are replaced atomically (delete-then-insert in a single `SaveChanges`)

**Given** the new `name` conflicts with an existing different role
**When** `PUT /api/admin/roles/{id}` is submitted
**Then** the response is HTTP 409 with `code: "ROLE_NAME_CONFLICT"`

**Given** I attempt to PUT a system role (`is_system = true`)
**When** `PUT /api/admin/roles/{id}` is submitted
**Then** the response is HTTP 409 with `ProblemDetails { code: "ROLE_SYSTEM_PROTECTED", messageKey: "roles.systemProtected" }`

**Given** role `{id}` does not exist
**When** `PUT /api/admin/roles/{id}` is submitted
**Then** the response is HTTP 404 with `code: "ROLE_NOT_FOUND"`

### AC-6 — `DELETE /api/admin/roles/{id}` deletes a role if safe

**Given** I am authenticated as a Platform Admin and the role has no active user assignments and is not a system role
**When** I `DELETE /api/admin/roles/{id}`
**Then** the response is HTTP 204 No Content
**And** the `roles` row and its `role_permissions` rows are deleted

**Given** the role has one or more rows in `user_roles`
**When** `DELETE /api/admin/roles/{id}` is submitted
**Then** the response is HTTP 409 with `ProblemDetails { code: "ROLE_HAS_ASSIGNMENTS", messageKey: "roles.hasAssignments" }`

**Given** the role is a system role (`is_system = true`)
**When** `DELETE /api/admin/roles/{id}` is submitted
**Then** the response is HTTP 409 with `ProblemDetails { code: "ROLE_SYSTEM_PROTECTED", messageKey: "roles.systemProtected" }`

**Given** role `{id}` does not exist
**When** `DELETE /api/admin/roles/{id}` is submitted
**Then** the response is HTTP 404 with `code: "ROLE_NOT_FOUND"`

### AC-7 — Admin endpoints require `platform-admin` role and are rate-limited

**Given** the `/api/admin` route group is configured (per AR-22 / Decision 3.5)
**When** any `/api/admin/*` request is processed
**Then** the filter chain order is: correlation ID → auth → rate limit → validation → handler
**And** the rate-limit policy is: sliding window, 120 requests per user per minute (per Decision 2.6)

### AC-8 — `AuthService` resolves role names from the database

**Given** the `user_roles` table now exists
**When** `LoginAsync` or `RefreshAsync` is called
**Then** `roleNames` is populated from `db.UserRoles.Where(ur => ur.UserId == user.Id).Select(ur => ur.Role.Name)`
**And** the `roles` claims in the issued JWT reflect the user's actual assigned roles
**And** for any user with no assignments the claim list is empty (same behavior as before, just from a real query)

---

## Tasks / Subtasks

> All conventions from Stories 2.1–2.3 remain in force: `internal sealed` on new types, `CA1812 [SuppressMessage]` on DI-injected classes, `ArgumentNullException.ThrowIfNull` on handler parameters, `InvariantGlobalization=true` (`StringComparison.Ordinal`, `.ToLowerInvariant()`), `TreatWarningsAsErrors=true`, CPM (`Directory.Packages.props` only), `[LoggerMessage]` source-gen for `ILogger` calls, single feature commit.

- [x] Task 1 — Domain entities (Role, RolePermission, UserRole) + User.UserRoles nav
- [x] Task 2 — DbContext mappings + three DbSets
- [x] Task 3 — Migration `20260523021147_CreateRolesRolePermissionsAndUserRoles` with HasData seeds for `platform-admin` + `viewer`
- [x] Task 4 — `RequireAuth()` + `RequirePlatformAdmin()` extension methods
- [x] Task 5 — Program.cs: `platform-admin` policy + `admin` sliding-window rate-limit + role-service registrations + `/api/admin` route group
- [x] Task 6 — Five DTOs under Features/Roles/Dtos/
- [x] Task 7 — `CreateRoleRequestValidator` + `UpdateRoleRequestValidator`
- [x] Task 8 — `IRoleService` / `RoleService` + `PagedResult<T>` in Common
- [x] Task 9 — `AdminEndpoints` dispatcher + `RoleEndpoints` (GET list, GET one, POST, PUT, DELETE)
- [x] Task 10 — `AuthService` queries real role names from `db.UserRoles` in both `LoginAsync` and `RefreshAsync`
- [x] Task 11 — 16 integration tests in `RoleIntegrationTests.cs` covering all five endpoints + all 4xx branches
- [x] Task 12 — Build (0 warn, 0 err), `dotnet format --verify-no-changes` clean, 105/105 tests green

### Task 1 — Domain entities: `Role`, `RolePermission`, `UserRole`

Create three files under `src/FormForge.Api/Domain/Entities/`.

**`Role.cs`:**
```csharp
namespace FormForge.Api.Domain.Entities;

internal sealed class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;           // stored lowercase-normalized
    public string? Description { get; set; }
    public bool IsSystem { get; set; }                         // platform-admin and viewer; cannot be mutated or deleted
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public ICollection<RolePermission> Permissions { get; set; } = [];
    public ICollection<UserRole> UserRoles { get; set; } = [];
}
```

**`RolePermission.cs`:**
```csharp
namespace FormForge.Api.Domain.Entities;

internal sealed class RolePermission
{
    public Guid Id { get; set; }
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public string ResourceId { get; set; } = string.Empty;     // designerId snake_case; max 63 chars
    public bool CanCreate { get; set; }
    public bool CanRead { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
}
```

**`UserRole.cs`:**
```csharp
namespace FormForge.Api.Domain.Entities;

internal sealed class UserRole
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
```

> **Why `UserRole` is created in this story:** The `user_roles` table is referenced in AC-6 (DELETE 409 check) and AC-8 (role-names query). The entity and migration must exist here even though assignment endpoints are Story 2.5. The `user_roles` table will be empty until 2.5 runs.

### Task 2 — Update `FormForgeDbContext`

Add `DbSet<Role>`, `DbSet<RolePermission>`, and `DbSet<UserRole>` to `FormForgeDbContext`. Also add a navigation property `public ICollection<UserRole> UserRoles { get; set; } = [];` to the `User` entity (in `User.cs`).

Add these mappings inside `OnModelCreating`, after the existing `RefreshToken` block:

```csharp
modelBuilder.Entity<Role>(e =>
{
    e.ToTable("roles");
    e.HasKey(r => r.Id);
    e.Property(r => r.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
    e.Property(r => r.Name).HasColumnName("name").IsRequired().HasMaxLength(100);
    e.Property(r => r.Description).HasColumnName("description").HasMaxLength(500);
    e.Property(r => r.IsSystem).HasColumnName("is_system").HasDefaultValue(false);
    e.Property(r => r.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
    e.Property(r => r.UpdatedAt).HasColumnName("updated_at");
    e.HasIndex(r => r.Name).IsUnique().HasDatabaseName("uq_roles_name");
});

modelBuilder.Entity<RolePermission>(e =>
{
    e.ToTable("role_permissions");
    e.HasKey(rp => rp.Id);
    e.Property(rp => rp.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
    e.Property(rp => rp.RoleId).HasColumnName("role_id").IsRequired();
    e.Property(rp => rp.ResourceId).HasColumnName("resource_id").IsRequired().HasMaxLength(63);
    e.Property(rp => rp.CanCreate).HasColumnName("can_create").HasDefaultValue(false);
    e.Property(rp => rp.CanRead).HasColumnName("can_read").HasDefaultValue(false);
    e.Property(rp => rp.CanUpdate).HasColumnName("can_update").HasDefaultValue(false);
    e.Property(rp => rp.CanDelete).HasColumnName("can_delete").HasDefaultValue(false);
    e.HasIndex(rp => new { rp.RoleId, rp.ResourceId })
     .IsUnique()
     .HasDatabaseName("uq_role_permissions_role_resource");
    e.HasOne(rp => rp.Role)
     .WithMany(r => r.Permissions)
     .HasForeignKey(rp => rp.RoleId)
     .HasConstraintName("fk_role_permissions_roles")
     .OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<UserRole>(e =>
{
    e.ToTable("user_roles");
    e.HasKey(ur => new { ur.UserId, ur.RoleId });
    e.Property(ur => ur.UserId).HasColumnName("user_id");
    e.Property(ur => ur.RoleId).HasColumnName("role_id");
    e.Property(ur => ur.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
    e.HasOne(ur => ur.User)
     .WithMany(u => u.UserRoles)
     .HasForeignKey(ur => ur.UserId)
     .HasConstraintName("fk_user_roles_users")
     .OnDelete(DeleteBehavior.Cascade);
    e.HasOne(ur => ur.Role)
     .WithMany(r => r.UserRoles)
     .HasForeignKey(ur => ur.RoleId)
     .HasConstraintName("fk_user_roles_roles")
     .OnDelete(DeleteBehavior.Cascade);
    e.HasIndex(ur => ur.RoleId).HasDatabaseName("idx_user_roles_role_id");
});
```

Also add `DbSet<Role> Roles => Set<Role>();`, `DbSet<RolePermission> RolePermissions => Set<RolePermission>();`, and `DbSet<UserRole> UserRoles => Set<UserRole>();` to the `FormForgeDbContext` property declarations.

### Task 3 — EF Core migration with system role seeding

Run `dotnet ef migrations add CreateRolesRolePermissionsAndUserRoles --project src/FormForge.Api` to generate the migration scaffold. After generation, add `HasData` seeding to the `roles` table inside the `Up` method. Use deterministic UUIDs so re-running the migration is idempotent:

```csharp
migrationBuilder.InsertData(
    table: "roles",
    columns: ["id", "name", "is_system", "created_at"],
    values: new object[,]
    {
        {
            new Guid("00000000-0000-0000-0000-000000000001"),
            "platform-admin",
            true,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture)
        },
        {
            new Guid("00000000-0000-0000-0000-000000000002"),
            "viewer",
            true,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture)
        },
    });
```

> **Why no per-resource rows for system roles:** Resources (designerIds) do not exist at migration time. Story 2.6 handles `platform-admin` and `viewer` via `is_system` special-case logic in the permission-computation engine — no `role_permissions` rows are needed for these roles. Non-system roles use per-resource rows; their permissions start empty and are configured via the API.

> **Why deterministic UUIDs (00000000-0000-0000-0000-000000000001/2):** `HasData` seeds require stable PKs so `MigrationBuilder.InsertData` can be idempotent and the `Down` migration can delete them by PK. The values will never be user-created (system roles are immutable by API). Using predictable UUIDs for seeded data is a standard EF Core seed pattern; do NOT use `gen_random_uuid()` which would regenerate on every migrate.

### Task 4 — `RequireAuth` and `RequirePlatformAdmin` route group extensions

Update `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`. Extend the existing `AddValidationFilter<T>` method with two new extension methods on `RouteGroupBuilder`:

```csharp
internal static RouteGroupBuilder RequireAuth(this RouteGroupBuilder group)
{
    ArgumentNullException.ThrowIfNull(group);
    group.RequireAuthorization();
    return group;
}

internal static RouteGroupBuilder RequirePlatformAdmin(this RouteGroupBuilder group)
{
    ArgumentNullException.ThrowIfNull(group);
    // Requires the "platform-admin" role claim (RoleClaimType = "roles" in JwtBearerOptions).
    group.RequireAuthorization("platform-admin");
    return group;
}
```

> **Why a named policy:** `RequireAuthorization(Action<AuthorizationPolicyBuilder>)` is not an overload on `RouteGroupBuilder`. Named policy (`"platform-admin"`) is the correct extension point. The policy is registered in Task 5.

### Task 5 — `Program.cs` updates

Three changes to `Program.cs`:

**a) Register the `"platform-admin"` authorization policy.** Change the current bare `builder.Services.AddAuthorization();` to:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("platform-admin", policy => policy.RequireRole("platform-admin"));
});
```

**b) Add the `"admin"` rate-limit policy** inside the existing `builder.Services.AddRateLimiter(options => { ... })` block, after the `"auth-refresh"` policy:

```csharp
options.AddPolicy("admin", httpContext =>
    RateLimitPartition.GetSlidingWindowLimiter(
        partitionKey: httpContext.User.FindFirst("userId")?.Value
                      ?? httpContext.Connection.RemoteIpAddress?.ToString()
                      ?? "unknown",
        factory: _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        }));
```

**c) Register the admin route group and role services.** After the existing auth service registrations (`AddScoped<IAuthService, AuthService>()` block) add:

```csharp
// Role services (Story 2.4)
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IValidator<CreateRoleRequest>, CreateRoleRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateRoleRequest>, UpdateRoleRequestValidator>();
builder.Services.AddScoped(typeof(ValidationFilter<>));
```

> `ValidationFilter<>` is already registered in the existing code — verify it exists before adding a second registration. If present, do not add a duplicate.

After the existing `app.MapGroup("/api/auth")...` line, add:

```csharp
app.MapGroup("/api/admin")
   .RequireAuth()
   .RequirePlatformAdmin()
   .RequireRateLimiting("admin")
   .WithTags("Admin")
   .MapAdminEndpoints();
```

Add the using for the new feature namespace at the top of the file:

```csharp
using FormForge.Api.Features.Roles;
using FormForge.Api.Features.Roles.Dtos;
using FormForge.Api.Features.Roles.Validators;
```

### Task 6 — DTOs

Create `src/FormForge.Api/Features/Roles/Dtos/`.

**`PermissionRecord.cs`:**
```csharp
namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record PermissionRecord(
    string ResourceId,
    bool CanCreate,
    bool CanRead,
    bool CanUpdate,
    bool CanDelete);
```

**`CreateRoleRequest.cs`:**
```csharp
namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record CreateRoleRequest(
    string Name,
    string? Description,
    IReadOnlyList<PermissionRecord> Permissions);
```

**`UpdateRoleRequest.cs`:**
```csharp
namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record UpdateRoleRequest(
    string Name,
    string? Description,
    IReadOnlyList<PermissionRecord> Permissions);
```

**`RoleListItem.cs`:**
```csharp
namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record RoleListItem(
    Guid Id,
    string Name,
    string? Description,
    int PermissionCount,
    bool IsSystem);
```

**`RoleResponse.cs`:**
```csharp
namespace FormForge.Api.Features.Roles.Dtos;

internal sealed record RoleResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystem,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<PermissionRecord> Permissions);
```

### Task 7 — Validators

Create `src/FormForge.Api/Features/Roles/Validators/`.

**`CreateRoleRequestValidator.cs`:**
```csharp
using FluentValidation;
using FormForge.Api.Features.Roles.Dtos;

namespace FormForge.Api.Features.Roles.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<CreateRoleRequest>.")]
internal sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100)
            .Matches(@"^[a-z][a-z0-9-]{0,98}[a-z0-9]$|^[a-z]$")
            .WithMessage("Role name must be lowercase alphanumeric with hyphens, 1–100 characters, no leading/trailing hyphen.");

        RuleFor(x => x.Description)
            .MaximumLength(500)
            .When(x => x.Description is not null);

        RuleForEach(x => x.Permissions).ChildRules(p =>
        {
            p.RuleFor(x => x.ResourceId)
             .NotEmpty()
             .MaximumLength(63)
             .Matches(@"^[a-z_][a-z0-9_]{0,61}[a-z0-9_]?$|^[a-z_]$")
             .WithMessage("ResourceId must be a valid snake_case identifier (max 63 chars).");
        });
    }
}
```

**`UpdateRoleRequestValidator.cs`:**
```csharp
using FluentValidation;
using FormForge.Api.Features.Roles.Dtos;

namespace FormForge.Api.Features.Roles.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<UpdateRoleRequest>.")]
internal sealed class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100)
            .Matches(@"^[a-z][a-z0-9-]{0,98}[a-z0-9]$|^[a-z]$")
            .WithMessage("Role name must be lowercase alphanumeric with hyphens, 1–100 characters, no leading/trailing hyphen.");

        RuleFor(x => x.Description)
            .MaximumLength(500)
            .When(x => x.Description is not null);

        RuleForEach(x => x.Permissions).ChildRules(p =>
        {
            p.RuleFor(x => x.ResourceId)
             .NotEmpty()
             .MaximumLength(63)
             .Matches(@"^[a-z_][a-z0-9_]{0,61}[a-z0-9_]?$|^[a-z_]$")
             .WithMessage("ResourceId must be a valid snake_case identifier (max 63 chars).");
        });
    }
}
```

> **Why role name format `^[a-z][a-z0-9-]{0,98}[a-z0-9]$|^[a-z]$`:** Role names appear as JWT role claims and are used in `.RequireRole("platform-admin")` string comparisons. Kebab-case lowercase is consistent with the seeded role names and avoids special characters that complicate HTTP header encoding. Single-character names are valid (`^[a-z]$` branch). The pattern is more permissive than the designerId pattern (hyphens allowed; underscores not required).

### Task 8 — `IRoleService` and `RoleService`

Create `src/FormForge.Api/Features/Roles/RoleService.cs`. This is the service layer containing all business logic; endpoints are thin delegates to this service.

```csharp
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Roles.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Roles;

internal enum CreateRoleOutcome { Success, DuplicateName }
internal sealed record CreateRoleResult(CreateRoleOutcome Outcome, Guid? RoleId = null);

internal enum UpdateRoleOutcome { Success, NotFound, DuplicateName, SystemProtected }
internal sealed record UpdateRoleResult(UpdateRoleOutcome Outcome);

internal enum DeleteRoleOutcome { Success, NotFound, HasAssignments, SystemProtected }
internal sealed record DeleteRoleResult(DeleteRoleOutcome Outcome);

internal interface IRoleService
{
    Task<PagedResult<RoleListItem>> GetRolesAsync(int page, int pageSize, CancellationToken ct);
    Task<RoleResponse?> GetRoleAsync(Guid id, CancellationToken ct);
    Task<CreateRoleResult> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct);
    Task<UpdateRoleResult> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct);
    Task<DeleteRoleResult> DeleteRoleAsync(Guid id, CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class RoleService(FormForgeDbContext db) : IRoleService
{
    public async Task<PagedResult<RoleListItem>> GetRolesAsync(int page, int pageSize, CancellationToken ct)
    {
        var total = await db.Roles.LongCountAsync(ct).ConfigureAwait(false);

        var items = await db.Roles
            .OrderBy(r => r.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new RoleListItem(
                r.Id,
                r.Name,
                r.Description,
                r.Permissions.Count,
                r.IsSystem))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<RoleListItem>(items, total, page, pageSize);
    }

    public async Task<RoleResponse?> GetRoleAsync(Guid id, CancellationToken ct)
    {
        var role = await db.Roles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        return role is null ? null : ToResponse(role);
    }

    public async Task<CreateRoleResult> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = request.Name.Trim().ToLowerInvariant();

        var exists = await db.Roles
            .AnyAsync(r => r.Name == normalized, ct)
            .ConfigureAwait(false);

        if (exists)
        {
            return new CreateRoleResult(CreateRoleOutcome.DuplicateName);
        }

        var role = new Role
        {
            Name = normalized,
            Description = request.Description?.Trim(),
            IsSystem = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        foreach (var p in request.Permissions)
        {
            role.Permissions.Add(new RolePermission
            {
                ResourceId = p.ResourceId.Trim().ToLowerInvariant(),
                CanCreate = p.CanCreate,
                CanRead = p.CanRead,
                CanUpdate = p.CanUpdate,
                CanDelete = p.CanDelete,
            });
        }

        db.Roles.Add(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new CreateRoleResult(CreateRoleOutcome.Success, role.Id);
    }

    public async Task<UpdateRoleResult> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var role = await db.Roles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        if (role is null)
        {
            return new UpdateRoleResult(UpdateRoleOutcome.NotFound);
        }

        if (role.IsSystem)
        {
            return new UpdateRoleResult(UpdateRoleOutcome.SystemProtected);
        }

        var normalized = request.Name.Trim().ToLowerInvariant();

        var nameConflict = await db.Roles
            .AnyAsync(r => r.Name == normalized && r.Id != id, ct)
            .ConfigureAwait(false);

        if (nameConflict)
        {
            return new UpdateRoleResult(UpdateRoleOutcome.DuplicateName);
        }

        role.Name = normalized;
        role.Description = request.Description?.Trim();
        role.UpdatedAt = DateTimeOffset.UtcNow;

        // Replace permissions atomically: remove all, re-add from request.
        db.RolePermissions.RemoveRange(role.Permissions);
        foreach (var p in request.Permissions)
        {
            role.Permissions.Add(new RolePermission
            {
                ResourceId = p.ResourceId.Trim().ToLowerInvariant(),
                CanCreate = p.CanCreate,
                CanRead = p.CanRead,
                CanUpdate = p.CanUpdate,
                CanDelete = p.CanDelete,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new UpdateRoleResult(UpdateRoleOutcome.Success);
    }

    public async Task<DeleteRoleResult> DeleteRoleAsync(Guid id, CancellationToken ct)
    {
        var role = await db.Roles
            .Include(r => r.UserRoles)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        if (role is null)
        {
            return new DeleteRoleResult(DeleteRoleOutcome.NotFound);
        }

        if (role.IsSystem)
        {
            return new DeleteRoleResult(DeleteRoleOutcome.SystemProtected);
        }

        if (role.UserRoles.Count > 0)
        {
            return new DeleteRoleResult(DeleteRoleOutcome.HasAssignments);
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new DeleteRoleResult(DeleteRoleOutcome.Success);
    }

    private static RoleResponse ToResponse(Role role) =>
        new(
            role.Id,
            role.Name,
            role.Description,
            role.IsSystem,
            role.CreatedAt,
            role.UpdatedAt,
            role.Permissions.Select(p =>
                new PermissionRecord(p.ResourceId, p.CanCreate, p.CanRead, p.CanUpdate, p.CanDelete))
            .ToList());
}
```

> **Why `db.RolePermissions.RemoveRange(role.Permissions)` instead of clearing the collection:** Clearing an EF navigation collection does not delete the rows unless cascade-delete is configured. `RemoveRange` explicitly marks each `RolePermission` entity for deletion, which is deterministic and visible in the generated SQL. The FK `ON DELETE CASCADE` on `role_permissions → roles` would also handle the DELETE case, but the explicit RemoveRange keeps the UpdateRoleAsync intent clear.

> **Note on `PagedResult<T>`:** This record is not yet defined in the codebase. Define it in `src/FormForge.Api/Common/` as:
> ```csharp
> namespace FormForge.Api.Common;
> internal sealed record PagedResult<T>(
>     IReadOnlyList<T> Data,
>     long Total,
>     int Page,
>     int PageSize)
> {
>     public int TotalPages => (int)Math.Ceiling((double)Total / PageSize);
> }
> ```
> Place at `src/FormForge.Api/Common/PagedResult.cs`. This will be reused by all future list endpoints.

### Task 9 — `AdminEndpoints` dispatcher and `RoleEndpoints`

Create `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` (the top-level admin dispatcher, expanded by future stories):

```csharp
namespace FormForge.Api.Features.Roles;

internal static class AdminEndpoints
{
    internal static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGroup("/roles").WithTags("Admin — Roles").MapRoleEndpoints();
        return group;
    }
}
```

Create `src/FormForge.Api/Features/Roles/RoleEndpoints.cs`:

```csharp
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Features.Roles.Dtos;
using FormForge.Api.Features.Roles.Validators;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FormForge.Api.Features.Roles;

internal static class RoleEndpoints
{
    internal static RouteGroupBuilder MapRoleEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/", GetRolesHandler)
             .WithSummary("List all roles (paginated)")
             .Produces<PagedResult<RoleListItem>>(StatusCodes.Status200OK);

        group.MapGet("/{id:guid}", GetRoleHandler)
             .WithSummary("Get a role by ID")
             .Produces<RoleResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateRoleHandler)
             .AddValidationFilter<CreateRoleRequest>()
             .WithSummary("Create a new role")
             .Produces<RoleResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{id:guid}", UpdateRoleHandler)
             .AddValidationFilter<UpdateRoleRequest>()
             .WithSummary("Update an existing role and replace its permissions")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{id:guid}", DeleteRoleHandler)
             .WithSummary("Delete a role (fails if it has active user assignments)")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<IResult> GetRolesHandler(
        IRoleService roleService,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roleService);
        pageSize = Math.Min(pageSize, 100);
        page = Math.Max(page, 1);
        var result = await roleService.GetRolesAsync(page, pageSize, ct).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetRoleHandler(
        Guid id,
        IRoleService roleService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleService);
        var role = await roleService.GetRoleAsync(id, ct).ConfigureAwait(false);
        return role is null
            ? Results.Problem(title: "Role not found", statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "ROLE_NOT_FOUND", ["messageKey"] = "roles.notFound" })
            : Results.Ok(role);
    }

    private static async Task<IResult> CreateRoleHandler(
        CreateRoleRequest request,
        IRoleService roleService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roleService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var result = await roleService.CreateRoleAsync(request, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            CreateRoleOutcome.DuplicateName => Results.Problem(
                title: "Role name already exists",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "ROLE_NAME_CONFLICT", ["messageKey"] = "roles.nameConflict" }),
            CreateRoleOutcome.Success => Results.Created(
                $"/api/admin/roles/{result.RoleId}",
                await roleService.GetRoleAsync(result.RoleId!.Value, ct).ConfigureAwait(false)),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> UpdateRoleHandler(
        Guid id,
        UpdateRoleRequest request,
        IRoleService roleService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roleService);

        var result = await roleService.UpdateRoleAsync(id, request, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            UpdateRoleOutcome.NotFound => Results.Problem(
                title: "Role not found",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "ROLE_NOT_FOUND", ["messageKey"] = "roles.notFound" }),
            UpdateRoleOutcome.SystemProtected => Results.Problem(
                title: "System roles cannot be modified",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "ROLE_SYSTEM_PROTECTED", ["messageKey"] = "roles.systemProtected" }),
            UpdateRoleOutcome.DuplicateName => Results.Problem(
                title: "Role name already exists",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "ROLE_NAME_CONFLICT", ["messageKey"] = "roles.nameConflict" }),
            UpdateRoleOutcome.Success => Results.NoContent(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> DeleteRoleHandler(
        Guid id,
        IRoleService roleService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleService);

        var result = await roleService.DeleteRoleAsync(id, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            DeleteRoleOutcome.NotFound => Results.Problem(
                title: "Role not found",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "ROLE_NOT_FOUND", ["messageKey"] = "roles.notFound" }),
            DeleteRoleOutcome.SystemProtected => Results.Problem(
                title: "System roles cannot be deleted",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "ROLE_SYSTEM_PROTECTED", ["messageKey"] = "roles.systemProtected" }),
            DeleteRoleOutcome.HasAssignments => Results.Problem(
                title: "Role has active user assignments",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["code"] = "ROLE_HAS_ASSIGNMENTS", ["messageKey"] = "roles.hasAssignments" }),
            DeleteRoleOutcome.Success => Results.NoContent(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
```

> **`Produces<PagedResult<RoleListItem>>` compile resolution:** `PagedResult<T>` must be in a namespace visible from `RoleEndpoints.cs`. Add a `using FormForge.Api.Common;` import.

> **`Results.Problem` with `extensions`:** The ProblemDetails middleware (set up in `Program.cs` via `AddProblemDetails`) already injects `correlationId` into every ProblemDetails response. The `extensions` dictionary adds `code` and `messageKey` per AR-18.

### Task 10 — Update `AuthService` to query roles from the database (AC-8)

In `src/FormForge.Api/Features/Auth/AuthService.cs`, replace **both** occurrences of:

```csharp
// Story 2.4 will populate role names from the roles + user_roles tables.
// For now, roles is always empty (no roles exist yet).
var roleNames = Array.Empty<string>();
```

Replace each with:

```csharp
var roleNames = await db.UserRoles
    .Where(ur => ur.UserId == user.Id)
    .Select(ur => ur.Role.Name)
    .ToArrayAsync(ct)
    .ConfigureAwait(false);
```

In `LoginAsync` the `user` variable already holds the authenticated user (line ~100). In `RefreshAsync` use `token.UserId` as the key: `.Where(ur => ur.UserId == token.UserId)`.

Also remove the now-obsolete comment on the `LoginAsync` occurrence. The query returns an empty array for users with no assignments, which is behaviorally identical to the previous `Array.Empty<string>()` for the seeded-but-unassigned state. No tests should regress.

> **Add to `FormForgeDbContext` usings in `AuthService.cs` if not present:** `using FormForge.Api.Domain.Entities;` is already present. The new query uses `db.UserRoles` (a new DbSet) — no new using required, just the new DbSet registered in Task 2.

### Task 11 — Integration tests for role endpoints

Create `src/FormForge.Api.Tests/Features/Roles/RoleIntegrationTests.cs`. Follow the same fixture pattern as `AuthIntegrationTests` — share the `PostgresFixture` via `IClassFixture<PostgresFixture>`, use `HandleCookies = false`, truncate between tests.

The test class needs a helper to obtain a platform-admin JWT. Seed a test user with the `platform-admin` role in `InitializeAsync`, then call `POST /api/auth/login` and capture the `AccessToken`.

**Truncation:** `TRUNCATE TABLE role_permissions, user_roles, roles, refresh_tokens, users RESTART IDENTITY CASCADE;` — then re-run system role seed via `db.Database.MigrateAsync()` (migration seeds are idempotent with fixed UUIDs).

Tests to write (cover each AC):

```
GetRoles_Unauthenticated_Returns401
GetRoles_AsNonAdmin_Returns403
GetRoles_AsPlatformAdmin_Returns200WithSystemRoles
GetRole_KnownId_Returns200WithPermissions
GetRole_UnknownId_Returns404
CreateRole_ValidBody_Returns201WithLocation
CreateRole_DuplicateName_Returns409NameConflict
CreateRole_EmptyName_Returns422ValidationProblem
UpdateRole_ValidBody_Returns204
UpdateRole_DuplicateName_Returns409
UpdateRole_SystemRole_Returns409SystemProtected
UpdateRole_UnknownId_Returns404
DeleteRole_NoAssignments_Returns204
DeleteRole_WithAssignments_Returns409HasAssignments
DeleteRole_SystemRole_Returns409SystemProtected
DeleteRole_UnknownId_Returns404
```

For `DeleteRole_WithAssignments_Returns409HasAssignments`: insert a row directly into `user_roles` via `db.UserRoles.Add(...)` + `db.SaveChangesAsync()` rather than going through Story 2.5's endpoint (which doesn't exist yet).

For `GetRoles_AsNonAdmin_Returns403`: seed a second user without the `platform-admin` role; log in as that user; call `GET /api/admin/roles` with that user's JWT.

> **Helper method shape:**
> ```csharp
> private async Task<string> GetPlatformAdminTokenAsync()
> {
>     using var response = await _client!.PostAsJsonAsync("/api/auth/login",
>         new { email = "admin@example.com", password = "Password1!" });
>     var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
>     return body!.AccessToken;
> }
> ```
> All protected endpoint calls must include `Authorization: Bearer {token}` header.

### Task 12 — Build gates and verification

- [ ] `dotnet build` — zero warnings, zero errors.
- [ ] `dotnet format --verify-no-changes` — clean.
- [ ] `dotnet test` — all existing tests pass; all new role integration tests pass.
- [ ] `dotnet ef migrations list` — new migration appears and is applied without errors.
- [ ] `GET /openapi/v1.json` includes all five `/api/admin/roles` paths with correct response codes.
- [ ] Manual: `POST /api/auth/login` as a user with `platform-admin` role — JWT `roles` claim contains `"platform-admin"`.
- [ ] Manual: `GET /api/admin/roles` with and without valid admin JWT — 200 vs 401/403.

### Review Findings

_Three-reviewer code review on commit `8a55795` (2026-05-23) — Blind Hunter, Edge Case Hunter, Acceptance Auditor._

**Patches (11)** — fixable without further input:

- [x] [Review][Patch] **Rate limiter middleware runs BEFORE authentication, so admin per-user partitioning silently degrades to per-IP — AC-7 violated.** [`src/FormForge.Api/Program.cs:285-287`] AC-7 mandates `correlation → auth → rate limit → validation → handler`. Current order is `UseRateLimiter` (285) → `UseAuthentication` (286) → `UseAuthorization` (287). When the partition factory reads `httpContext.User.FindFirst("userId")`, the principal is still anonymous → falls back to `RemoteIpAddress`. Multiple admins behind one NAT share one 120/min bucket. Move `UseRateLimiter()` to run after `UseAuthorization()`. (sources: blind+edge+auditor)
- [x] [Review][Patch] **Concurrent `POST /api/admin/roles` with the same name returns 500 instead of 409.** [`src/FormForge.Api/Features/Roles/RoleService.cs:67-97`] `AnyAsync` check + `SaveChangesAsync` is non-atomic. Two concurrent requests both pass the existence check, the second insert violates `uq_roles_name` and Npgsql throws `DbUpdateException` (PostgresException 23505). No catch handler → unhandled → 500. Wrap `SaveChangesAsync` in try/catch, inspect `inner is PostgresException { SqlState: "23505" }` on `uq_roles_name`, and return `CreateRoleOutcome.DuplicateName`. Same fix in `UpdateRoleAsync` (lines 123-129). (sources: blind+edge)
- [x] [Review][Patch] **Duplicate `resourceId` in `Permissions` returns 500 instead of 422.** [`src/FormForge.Api/Features/Roles/Validators/CreateRoleRequestValidator.cs` + `UpdateRoleRequestValidator.cs`] Validators do not enforce distinct ResourceIds. Service normalizes with `.Trim().ToLowerInvariant()` then inserts duplicate rows; `uq_role_permissions_role_resource` throws an unhandled `DbUpdateException`. Add `RuleFor(x => x.Permissions).Must(p => p == null || p.Select(x => x.ResourceId.Trim().ToLowerInvariant()).Distinct().Count() == p.Count).WithMessage("Duplicate resourceId in permissions.")` to both validators. (sources: blind+edge)
- [x] [Review][Patch] **Null `permissions` JSON property crashes with `NullReferenceException` → 500.** [`src/FormForge.Api/Features/Roles/Dtos/CreateRoleRequest.cs` + validators + `RoleService.cs:84,141`] Positional record default for `IReadOnlyList<>` is `null`. Service's `foreach (var p in request.Permissions)` NREs. `RuleForEach` on a null collection silently no-ops in FluentValidation. Add `RuleFor(x => x.Permissions).NotNull()` to both validators. (sources: edge)
- [x] [Review][Patch] **Page-number integer overflow → 500.** [`src/FormForge.Api/Features/Roles/RoleService.cs:37`] `(page - 1) * pageSize` is `int * int`; for `page = int.MaxValue` and `pageSize = 100`, the product overflows to a negative number and `Skip(negative)` throws `ArgumentOutOfRangeException`. Cast one operand to `long` and use `.Skip((int)Math.Min(int.MaxValue, ((long)page - 1) * pageSize))`, or clamp `page` to `Math.Min(page, MaxPage)` derived from `total`. (sources: edge)
- [x] [Review][Patch] **Unbounded `Permissions` array size.** [`src/FormForge.Api/Features/Roles/Validators/*.cs`] No upper bound on `Permissions.Count`. Kestrel caps body at 30MB but a 30MB valid payload is still iterated and inserted row-by-row. Add `RuleFor(x => x.Permissions).Must(p => p == null || p.Count <= 200).WithMessage("Too many permissions in one request (max 200).")` to both validators. (sources: edge)
- [x] [Review][Patch] **`ResourceId` regex admits trailing underscore and lone underscore.** [`src/FormForge.Api/Features/Roles/Validators/CreateRoleRequestValidator.cs:27` + `UpdateRoleRequestValidator.cs:27`] Current `^[a-z_][a-z0-9_]{0,61}[a-z0-9_]?$|^[a-z_]$` accepts `"incident_report_"` (trailing `_`) and `"_"` (lone `_`) — neither matches the snake_case convention. Tighten to `^[a-z][a-z0-9_]{0,61}[a-z0-9]$|^[a-z]$`. (sources: edge)
- [x] [Review][Patch] **Role-name regex allows consecutive hyphens (`a--b`).** [`src/FormForge.Api/Features/Roles/Validators/CreateRoleRequestValidator.cs:15` + `UpdateRoleRequestValidator.cs:15`] Add `.Must(name => string.IsNullOrEmpty(name) || !name.Contains("--", StringComparison.Ordinal)).WithMessage("Role name cannot contain consecutive hyphens.")` after the existing `.Matches(...)` rule. (sources: blind)
- [x] [Review][Patch] **`CreateRoleHandler` issues a redundant second `GetRoleAsync` after success — race window where the row may already be deleted, producing 201 with `null` body.** [`src/FormForge.Api/Features/Roles/RoleEndpoints.cs:84-86` + `RoleService.cs:61-100`] Change `CreateRoleAsync` to return the freshly-persisted `RoleResponse` (or extend `CreateRoleResult` to carry it). Eliminate the second roundtrip in the handler. (sources: blind+edge)
- [x] [Review][Patch] **`Description = ""` is stored as empty string (not null), breaking round-trip idempotency.** [`src/FormForge.Api/Features/Roles/RoleService.cs:79,133`] Validator's `When(x => x.Description is not null)` accepts `""`. Service writes `"".Trim() == ""`. Normalize to null: `Description = request.Description?.Trim() is { Length: > 0 } d ? d : null`. (sources: edge)
- [x] [Review][Patch] **`GET /api/admin/roles/{id}` returns `permissions` in arbitrary order.** [`src/FormForge.Api/Features/Roles/RoleService.cs:194`] `Include(r => r.Permissions)` has no ORDER BY → Postgres returns rows in heap order; successive GETs may differ. SPA diffing will see spurious changes. Add `.OrderBy(p => p.ResourceId)` inside `ToResponse`. (sources: edge)

**Deferred (5)** — real but not actionable in this story:

- [x] [Review][Defer] **Race between `DELETE role` and `POST user-role assignment` silently destroys the new assignment via `ON DELETE CASCADE`.** [`src/FormForge.Api/Features/Roles/RoleService.cs:158-184`] — deferred: needs transactional locking (`SELECT … FOR UPDATE`) or a `Role.RowVersion` concurrency token. Story 2.5 introduces the assignment endpoints — fix it there alongside the new write path.
- [x] [Review][Defer] **No optimistic-concurrency token on `Role` updates — concurrent PUTs silently last-write-win.** [`src/FormForge.Api/Domain/Entities/Role.cs` + `RoleService.cs:102-156`] — deferred: requires schema change (`xmin` mapped as `RowVersion` or a dedicated column) plus ETag/If-Match flow. Out of scope for Story 2.4 acceptance criteria; reconsider during Story 2.5/2.6 admin hardening pass.
- [x] [Review][Defer] **`CreateRoleHandler` hardcodes `/api/admin/roles/{id}` in `Location` header — won't honor a host path-base prefix.** [`src/FormForge.Api/Features/Roles/RoleEndpoints.cs:85`] — deferred: app currently has no path-base prefix and `LinkGenerator` wiring is broader than this story. Defensive-only.
- [x] [Review][Defer] **`AuthService.LoginAsync` / `RefreshAsync` add a second DB roundtrip for roles instead of `.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)`.** [`src/FormForge.Api/Features/Auth/AuthService.cs:100-102,190-191`] — deferred: hot-path perf optimization; current shape matches spec wording exactly. Revisit during a broader auth-perf pass.
- [x] [Review][Defer] **`RefreshAsync` race with `IsActive` deactivation — a just-deactivated admin can still mint a fresh 15-min access token.** [`src/FormForge.Api/Features/Auth/AuthService.cs`] — deferred: pre-existing limitation, not introduced by Story 2.4. Token-revocation strategy is a separate concern.

**Dismissed (12, recorded for traceability):** ORDER BY name without tiebreaker (unique idx already), `MapGet("/")` trailing-slash (normalized by routing, tests green), N+1 on `r.Permissions.Count` (EF 8+ subquery is correct), reseed-system-roles parallelism (xUnit class fixture serializes within a class), `MapInboundClaims = false` "fragile coupling" (debug-logged + token writer is centralized), `RequireAuth + RequirePlatformAdmin` redundancy (intentional for readability), system-role protection check after `Include` (bounded by new Permissions count cap), `PagedResult.TotalPages` int overflow (unrealistic for `roles` table), case-sensitive validator regex (matches AC-3 lowercase example), TRUNCATE list maintenance (acceptable test convention), `DeleteRoleAsync` doesn't load Permissions (FK cascade handles it), auditor's `token.User` loading note (not a violation per the auditor itself).

---

## Dev Notes

### What this story does — and what it does NOT

**In scope:**
1. `roles`, `role_permissions`, `user_roles` tables (migration + EF mappings).
2. System-role seeding: `platform-admin` and `viewer` with stable UUIDs.
3. `IRoleService` / `RoleService` with full CRUD business logic.
4. Five admin endpoints: GET list, GET single, POST, PUT, DELETE.
5. `RequireAuth()` and `RequirePlatformAdmin()` route group extension methods.
6. `"platform-admin"` authorization policy in `AddAuthorization()`.
7. `"admin"` sliding-window rate-limit policy (120/min per user).
8. `PagedResult<T>` common record.
9. `AuthService` update: real role-name query replaces `Array.Empty<string>()`.
10. Integration tests (Testcontainers) for all five endpoints and all 409/404 paths.

**Out of scope (deferred to named stories):**
- Assigning roles to users — Story 2.5.
- Effective permission computation and endpoint authorization — Story 2.6.
- `IDomainEventBus` / `RolePermissionsChanged` event for cache invalidation — Story 2.6 introduces the event bus; Story 2.5 fires the first event. Story 2.4 does NOT publish events (no cache exists yet).
- Permission cache (`IMemoryCache` + `EffectivePermissions`) — Story 2.6.
- Frontend Role Management UI — Story 2.8.
- `/health` platform-admin auth guard — Story 2.6 (AR-25).
- Bulk role-permission import — not in PRD.

### Architecture compliance

- **Decision 3.4 (PagedResult):** `PagedResult<T>` matches the spec exactly: `Data`, `Total`, `Page`, `PageSize`, `TotalPages`.
- **Decision 3.5 (Route groups):** `/api/admin` group uses `RequireAuth() + RequirePlatformAdmin() + RequireRateLimiting("admin") + MapAdminEndpoints()`. Future admin stories add `MapXxxEndpoints()` calls inside `AdminEndpoints.MapAdminEndpoints`.
- **Decision 2.6 (Rate limiting):** Sliding window, 120/min per user for `/api/admin/*`. Partition key prefers `userId` claim (available post-auth); falls back to IP for anonymous requests that slip through (they will be rejected by `RequireAuth()` anyway, so IP fallback is defensive only).
- **Decision 3.1 (ProblemDetails):** All 4xx responses use `Results.Problem(...)` with `code` and `messageKey` in the `extensions` dictionary. The global `AddProblemDetails` middleware injects `correlationId`.
- **Decision 1.7 (Migration tooling):** EF Core migration covers `roles`, `role_permissions`, `user_roles`. Dapper never touches these tables. The `Database.Migrate()` on startup is idempotent.
- **AR-22 (Filter chain):** The admin group-level filters run in middleware order: auth (from `RequireAuth()`) → rate-limit (from `RequireRateLimiting("admin")`) → validation (per-endpoint `AddValidationFilter<T>()`) → handler. No group-level permission filter yet (Story 2.6).
- **NFR-4 (auth required):** Admin endpoints return 401 when JWT is absent/invalid; 403 when JWT is valid but role is missing. Both are handled by the combined `RequireAuth() + RequirePlatformAdmin()` filters.
- **Decision 2.2 (Permission cache, `RolePermissionsChanged`):** Story 2.4 does NOT publish `RolePermissionsChanged` events because the permission cache does not exist yet. The `UpdateRoleAsync` and `DeleteRoleAsync` service methods are correct as written. Story 2.6 will introduce the event bus and Story 2.6 will also need to retroactively wire the event into these service methods — this is a known follow-on task, not a deficiency in Story 2.4.

### Current code state — files being modified

**`src/FormForge.Api/Domain/Entities/User.cs`** — Add `public ICollection<UserRole> UserRoles { get; set; } = [];` navigation property.

**`src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`** — Add three new DbSets; add three new `modelBuilder.Entity<>` blocks inside `OnModelCreating`.

**`src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`** — Add `RequireAuth()` and `RequirePlatformAdmin()` extension methods on `RouteGroupBuilder`.

**`src/FormForge.Api/Program.cs`** — Add authorization policy, admin rate-limit policy, role service registrations, admin route group registration.

**`src/FormForge.Api/Features/Auth/AuthService.cs`** — Replace two `Array.Empty<string>()` occurrences with real `db.UserRoles` queries.

### New files

| File | Purpose |
|------|---------|
| `src/FormForge.Api/Domain/Entities/Role.cs` | Role entity |
| `src/FormForge.Api/Domain/Entities/RolePermission.cs` | Per-resource CRUD flags entity |
| `src/FormForge.Api/Domain/Entities/UserRole.cs` | User↔Role join entity |
| `src/FormForge.Api/Common/PagedResult.cs` | Shared pagination response record |
| `src/FormForge.Api/Features/Roles/RoleService.cs` | IRoleService + RoleService |
| `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` | Top-level admin route dispatcher |
| `src/FormForge.Api/Features/Roles/RoleEndpoints.cs` | /api/admin/roles endpoint handlers |
| `src/FormForge.Api/Features/Roles/Dtos/PermissionRecord.cs` | DTO |
| `src/FormForge.Api/Features/Roles/Dtos/CreateRoleRequest.cs` | DTO |
| `src/FormForge.Api/Features/Roles/Dtos/UpdateRoleRequest.cs` | DTO |
| `src/FormForge.Api/Features/Roles/Dtos/RoleListItem.cs` | DTO |
| `src/FormForge.Api/Features/Roles/Dtos/RoleResponse.cs` | DTO |
| `src/FormForge.Api/Features/Roles/Validators/CreateRoleRequestValidator.cs` | FluentValidation |
| `src/FormForge.Api/Features/Roles/Validators/UpdateRoleRequestValidator.cs` | FluentValidation |
| `src/FormForge.Api.Tests/Features/Roles/RoleIntegrationTests.cs` | Integration tests |
| `src/FormForge.Api/Infrastructure/Persistence/Migrations/2026XXXXXX_CreateRolesRolePermissionsAndUserRoles.cs` | Generated migration |

### Do NOT touch

- `src/FormForge.Api/Features/Auth/JwtTokenService.cs` — already accepts `IReadOnlyList<string> roleNames`; no change needed.
- `src/FormForge.Api/Features/Auth/AuthMetrics.cs` — no new metrics in this story.
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — no changes.
- Any existing EF Core migration files.
- `web/src/**` — no frontend work in this story; UI is Story 2.8.
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — updated automatically by `dotnet ef migrations add`.

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Store system-role permissions as per-resource rows in `role_permissions` | Resources don't exist at migration time. Story 2.6 handles `is_system` roles via special-case logic in the permission engine. Seeding fake resource permissions would need to be cleaned up every time a new resource is added. |
| Use random UUIDs in `InsertData` for seeded roles | Random UUIDs make migration non-idempotent; re-running `Database.Migrate()` would try to insert duplicate rows. Use fixed deterministic UUIDs (`00000000-0000-0000-0000-000000000001/2`). |
| Allow renaming or deleting system roles | `platform-admin` and `viewer` are referenced by name in `RequirePlatformAdmin()` and Story 2.6's permission engine. Renaming them silently breaks authorization. `IsSystem = true` check in `UpdateRoleAsync` and `DeleteRoleAsync` prevents this. |
| Query role names in `AuthService` using `db.Roles.Where(r => r.UserRoles.Any(ur => ur.UserId == userId))` | This produces a more complex SQL than `db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.Role.Name)`. The second form is a flat join from the FK side and generates a simple `INNER JOIN` without a subquery. |
| Add `RolePermissionsChanged` event publishing in Story 2.4 | The event bus (`IDomainEventBus`) and permission cache don't exist until Story 2.6. Publishing an event to a null bus would require null-guarding or an empty bus stub. Leave the event wiring entirely to Story 2.6 (it will patch `UpdateRoleAsync` and `DeleteRoleAsync`). |
| Call `MapAdminEndpoints()` directly on the `/api/admin` route in each feature | `MapAdminEndpoints()` is the single registration point. Future stories (2.8, 2.9) add `MapUserEndpoints()` and `MapRoleManagementUiEndpoints()` as sub-routes inside `AdminEndpoints.MapAdminEndpoints`. Do not create parallel top-level route group registrations for admin sub-features. |
| Use `RouteHandlerBuilder` instead of `RouteGroupBuilder` for `RequireAuth/RequirePlatformAdmin` | The extension target type must be `RouteGroupBuilder` (group-level policy) not `RouteHandlerBuilder` (per-route policy). Applying at the group level means every handler in `/api/admin/*` inherits the policy automatically. |
| Return 422 for 409 scenarios (duplicate name, system role) | 409 Conflict is the correct status for "state prevents action" (pre-existing resource with same name, system constraint). 422 is for "request body is syntactically/semantically invalid". |
| Expose `is_system` as a mutable field in `CreateRoleRequest` or `UpdateRoleRequest` | Users must never be able to create or promote a role to system status via the API. `IsSystem` is only set by the migration seeding. |

### Previous story intelligence (Story 2.3 carry-forward)

- `TRUNCATE TABLE ... RESTART IDENTITY CASCADE` pattern — new test must extend to include `role_permissions, user_roles, roles` before `refresh_tokens, users`.
- `HandleCookies = false` in `WebApplicationFactoryClientOptions` — still required; new tests that use JWT auth add `Authorization: Bearer {token}` headers manually.
- `LoginResponseDto` and `LoginAndGetTokensAsync` helpers — reuse from `AuthIntegrationTests`. Consider extracting to a shared test helper class if it hasn't been done yet, but don't create the extraction as scope-creep; just call the method by duplicating if needed.
- `CA1812` suppression on DI-injected types — required on `RoleService`, both validators, and the new `ValidationFilter<>` if it's a new instantiation.
- `partial class Program` at the bottom of `Program.cs` — already present; `WebApplicationFactory<Program>` resolution in tests depends on it. Do NOT remove.
- `TreatWarningsAsErrors=true` — any new LINQ or EF query that pulls more data than needed will be caught by the compiler or tests; use `.Select()` projections to avoid materializing full entities in list queries.

### Git intelligence

Recent commits (most recent first):
- `f45a78c` — Story 2.3 code review — apply 2 patches from three-reviewer pass
- `6e4afa8` — Story 2.3 — Logout: POST /api/auth/logout + useLogoutMutation + AppLayout logout button
- `8cac5fa` — Story 2.2 code review — apply 15 patches from three-reviewer pass
- `88b2f9e` — Story 2.2 — Token refresh: POST /api/auth/refresh + 401-retry in httpClient + useAuthQuery SPA boot + OTel metrics

**Expected commit message for this story:**
`Story 2.4 — Role CRUD: /api/admin/roles CRUD + roles/role_permissions/user_roles schema + AuthService role query`

### Testing approach

**Integration tests only (Testcontainers, real Postgres)** — same as Stories 2.1–2.3. All five endpoints and all business-logic branches (409s, 404s, 403s, 401) covered.

**No unit tests:** `RoleService` is primarily DB interaction. `ValidationFilter<T>` and validators are already tested indirectly by the 422 assertions.

**No frontend tests:** No frontend changes in this story.

**Seeding strategy for tests:**
- `platform-admin` user: seeded via `db.Users.Add(...)` + `db.UserRoles.Add(...)` directly in `InitializeAsync`.
- `viewer` user (for 403 tests): seeded the same way without a `platform-admin` user-role row.
- Roles: the migration already seeds `platform-admin` and `viewer` system roles. `db.Database.MigrateAsync()` applies them.

### References

- `_bmad-output/planning-artifacts/epics.md` — §"Story 2.4: Role CRUD"
- `_bmad-output/planning-artifacts/architecture.md`
  - Decision 1.7 — Migration tooling (EF covers `roles`, `role_permissions`, `user_roles`)
  - Decision 2.2 — Permission cache + `EffectivePermissions` record + bust events
  - Decision 2.6 — Rate limiting: `/api/admin/*` sliding 120/min per user
  - Decision 3.1 — RFC 7807 ProblemDetails with `code` + `messageKey`
  - Decision 3.4 — `PagedResult<T>` shape
  - Decision 3.5 — Route group organization: `app.MapGroup("/api/admin").RequireAuth().RequirePlatformAdmin().MapAdminEndpoints()`
  - AR-11 — Permission cache invalidation events (wired in Story 2.6, not here)
  - AR-15 / AR-22 — Rate limiting + route groups
  - AR-25 — `/health` platform-admin guard (Story 2.6)
- `_bmad-output/implementation-artifacts/2-3-logout.md` — carry-forward patterns (HandleCookies, TRUNCATE order, CA1812)
- `src/FormForge.Api/Features/Auth/AuthService.cs:100-102` and `:190-191` — the two `Array.Empty<string>()` placeholders replaced in Task 10

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (1M context) via Claude Code, bmad-dev-story workflow.

### Debug Log References

- **JWT role-claim invisible on principal — fixed by `JwtBearerOptions.MapInboundClaims = false`.**
  First test run: every authenticated admin request returned 403 even though the JWT
  payload contained `"roles":"platform-admin"` (verified by a temporary diagnostic
  test that base64-decoded the access token). Root cause: `JwtSecurityTokenHandler`
  defaults to mapping inbound short claim names through `DefaultInboundClaimTypeMap`
  before the `ClaimsIdentity` is built — even with `TokenValidationParameters.RoleClaimType = "roles"`,
  the "roles" claim was being rewritten and the principal never saw a claim of type
  "roles", so both `RequireRole("platform-admin")` and `RequireClaim("roles", "platform-admin")`
  failed. Disabling `MapInboundClaims` makes the JsonWebTokenHandler / JwtSecurityTokenHandler
  leave the custom claim names alone. This is a one-line addition to `AddJwtBearer`
  options in `Program.cs` and does not affect Stories 2.1–2.3 (which never read
  the role claim).
- **Empty migration scaffold from `--no-build` — fixed by re-running with build.**
  First `dotnet ef migrations add ... --no-build` produced an empty Up/Down because
  EF resolved the model against the stale (pre-Role) assembly. Removing the empty
  migration and re-running without `--no-build` produced the correct scaffold.
- **CA1814 / CA1861 on EF-generated migration code.**
  `InsertData` with multidimensional `object[,]` and `columns: new[] { ... }` trip
  CA1814 + CA1861 when `TreatWarningsAsErrors=true`. Suppressed both for the
  `Migrations/` folder in `.editorconfig` (alongside the pre-existing CA1515
  suppression) since these are generated patterns.

### Completion Notes List

1. **System role seed uses deterministic UUIDs** (`00000000-0000-0000-0000-000000000001`
   and `…000000000002`) so `Database.Migrate()` is idempotent across restarts and
   the `Down` migration can `DeleteData` by PK. Story 2.6 will special-case
   `is_system = true` rows in the permission engine; no per-resource rows are
   seeded for system roles because designerIds don't exist at migration time.
2. **`/api/admin/*` filter chain** = correlation-id → auth (`RequireAuth`) → admin
   role (`RequirePlatformAdmin`) → rate-limit (`admin` policy, sliding window 120/min
   partitioned by `userId` claim) → per-endpoint validation filter → handler.
3. **`UpdateRoleAsync` permission replacement** uses explicit
   `db.RolePermissions.RemoveRange(role.Permissions)` (not collection.Clear) so the
   DELETE is deterministic and visible in the generated SQL — the cascade FK would
   also handle it, but the explicit form documents the intent.
4. **`AuthService` now queries `db.UserRoles.Where(ur => ur.UserId == X).Select(ur => ur.Role.Name)`**
   in both `LoginAsync` and `RefreshAsync`. EF generates a flat `INNER JOIN` (confirmed
   in test logs). Empty array for unassigned users — same behavior as before, just
   from a real query.
5. **`PagedResult<T>`** placed at `src/FormForge.Api/Common/PagedResult.cs`. The
   `TotalPages` getter guards against `PageSize <= 0` returning 0 instead of throwing
   `DivideByZeroException`.
6. **No `RolePermissionsChanged` event emission** in Story 2.4 — `IDomainEventBus`
   doesn't exist yet (Story 2.6). The TODO is implicit: Story 2.6 will need to add
   event publishing to `UpdateRoleAsync` and `DeleteRoleAsync`.
7. **Test fixture re-seeds system roles after TRUNCATE** via a helper rather than
   re-running migrations, because `TRUNCATE … CASCADE` wipes the migration-seeded
   rows and `db.Database.MigrateAsync()` on an already-migrated DB is a no-op (it
   won't re-run InsertData). The helper inserts the same two UUIDs the migration
   would, so test runs match production state.

### File List

**New files:**
- `src/FormForge.Api/Domain/Entities/Role.cs`
- `src/FormForge.Api/Domain/Entities/RolePermission.cs`
- `src/FormForge.Api/Domain/Entities/UserRole.cs`
- `src/FormForge.Api/Common/PagedResult.cs`
- `src/FormForge.Api/Features/Roles/RoleService.cs`
- `src/FormForge.Api/Features/Roles/AdminEndpoints.cs`
- `src/FormForge.Api/Features/Roles/RoleEndpoints.cs`
- `src/FormForge.Api/Features/Roles/Dtos/PermissionRecord.cs`
- `src/FormForge.Api/Features/Roles/Dtos/CreateRoleRequest.cs`
- `src/FormForge.Api/Features/Roles/Dtos/UpdateRoleRequest.cs`
- `src/FormForge.Api/Features/Roles/Dtos/RoleListItem.cs`
- `src/FormForge.Api/Features/Roles/Dtos/RoleResponse.cs`
- `src/FormForge.Api/Features/Roles/Validators/CreateRoleRequestValidator.cs`
- `src/FormForge.Api/Features/Roles/Validators/UpdateRoleRequestValidator.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523021147_CreateRolesRolePermissionsAndUserRoles.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523021147_CreateRolesRolePermissionsAndUserRoles.Designer.cs`
- `src/FormForge.Api.Tests/Features/Roles/RoleIntegrationTests.cs`

**Modified files:**
- `src/FormForge.Api/Domain/Entities/User.cs` — added `UserRoles` navigation
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — added 3 DbSets + 3 entity mapping blocks
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — regenerated by `dotnet ef migrations add`
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs` — added `RequireAuth()` + `RequirePlatformAdmin()`
- `src/FormForge.Api/Program.cs` — `MapInboundClaims = false`, `platform-admin` authz policy, `admin` rate-limit policy, role service registrations, `/api/admin` route group, new usings
- `src/FormForge.Api/Features/Auth/AuthService.cs` — replaced both `Array.Empty<string>()` placeholders with `db.UserRoles` join
- `.editorconfig` — widened Migrations-folder analyzer suppressions to include CA1814 + CA1861

### Change Log

- 2026-05-23 — Story 2.4 created (ready-for-dev). Story context engine analysis completed.
- 2026-05-23 — Story 2.4 implemented (review). 17 new files, 7 modified. 16 new integration tests; full suite 105/105 green. Includes `MapInboundClaims = false` fix on JwtBearer so the custom `roles` JWT claim survives onto the `ClaimsPrincipal` — this affects every existing endpoint that ever needed to read a role (none in 2.1–2.3, all admin endpoints from 2.4 onward).
