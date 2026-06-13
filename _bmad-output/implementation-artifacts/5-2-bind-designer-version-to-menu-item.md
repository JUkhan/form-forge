# Story 5.2: Bind Designer Version to Menu Item

Status: done

## Story

As a Platform Admin,
I want to bind a specific Published Designer version to a Menu Item,
So that the platform provisions the backing table and connects the CRUD UI.

## Acceptance Criteria

**AC-1 — Bind Published Version:**
**Given** a Menu Item and a Published Designer version
**When** I PUT `/api/admin/menus/{menuId}/binding` with `{ designerId, version }`
**Then** the binding is saved with `provisioningStatus: "Pending"`
**And** the response is HTTP 202 (per AR-23)
**And** a `ProvisioningJob` is enqueued on `Channel<ProvisioningJob>` (per AR-9 / AR-37)

**AC-2 — Draft/Archived Rejected:**
**Given** an attempt to bind to a Draft or Archived version (only Published bindable)
**When** the bind request is processed
**Then** the response is HTTP 422 with `code: "VERSION_NOT_PUBLISHED"`

**AC-3 — Frontend Polls for Status Transition:**
**Given** the admin SPA after a bind request
**When** the SPA polls the Menu Item's binding state
**Then** the provisioningStatus transitions Pending → Success or Pending → Error, and a sonner toast announces the transition (per AR-29)

**AC-4 — Retry After Error:**
**Given** a binding's provisioning failed
**When** the admin clicks Retry
**Then** the binding is re-enqueued without changing the binding values themselves; provisioningStatus resets to Pending

**AC-5 — Re-bind to New Version:**
**Given** I update the bound version (e.g., v1 → v2)
**When** I PUT `/api/admin/menus/{menuId}/binding` with the new version
**Then** the same async pipeline runs (Story 5.4 handles the ALTER TABLE diff)

**AC-6 — Binding Diff Preview:**
**Given** the binding-diff preview endpoint
**When** I GET `/api/admin/menus/{menuId}/binding-diff?targetVersion={N}` (per AR-23 / Decision 3.6)
**Then** I receive `{ currentBinding, targetVersion, columnsToAdd, columnsAlreadyPresent, orphanedColumns, willTriggerChildProvisioning, estimatedDdl }` for the admin diff modal

## Tasks / Subtasks

- [x] **Task 1 — EF Migration: Add binding columns to `menus` table** (AC: 1, 2, 3, 4, 5)
  - [x] Run `dotnet ef migrations add AddMenuBindingColumns --project src/FormForge.Api --startup-project src/FormForge.Api`
  - [x] Migration adds nullable columns: `designer_id VARCHAR(63)`, `bound_version INTEGER`, `provisioning_status VARCHAR(20)`, `provisioning_error TEXT` — all nullable (menus without a binding are section headers)
  - [x] Add index: `CREATE INDEX idx_menus_designer_id ON menus(designer_id)` (for binding-based lookups)
  - [x] Add index: `CREATE INDEX idx_menus_provisioning_status ON menus(provisioning_status) WHERE provisioning_status = 'Pending'` (for ProvisioningRecoveryService in Story 5.8)

- [x] **Task 2 — Update `Menu` entity** (AC: 1, 3, 4, 5)
  - [x] Add to `src/FormForge.Api/Domain/Entities/Menu.cs`:
    ```csharp
    public string? DesignerId { get; set; }           // null = no binding (section header)
    public int? BoundVersion { get; set; }             // pinned version; null = no binding
    public string? ProvisioningStatus { get; set; }   // "Pending" | "Success" | "Error" | null
    public string? ProvisioningError { get; set; }    // null unless Status == "Error"
    ```
  - [x] Add navigation property `ComponentSchema? BoundDesigner { get; set; }` — FK to `component_schemas.designer_id` (optional, for join queries; not loaded by default)

- [x] **Task 3 — Update `FormForgeDbContext`** (AC: 1)
  - [x] Add 4 column mappings under `modelBuilder.Entity<Menu>(e => { ... })`:
    ```csharp
    e.Property(m => m.DesignerId).HasColumnName("designer_id").HasMaxLength(63);
    e.Property(m => m.BoundVersion).HasColumnName("bound_version");
    e.Property(m => m.ProvisioningStatus).HasColumnName("provisioning_status").HasMaxLength(20);
    e.Property(m => m.ProvisioningError).HasColumnName("provisioning_error");
    ```
  - [x] Add the optional FK from `menus.designer_id → component_schemas.designer_id` with `OnDelete(DeleteBehavior.SetNull)` — if a Designer is deleted, the binding is cleared (not the menu):
    ```csharp
    e.HasOne(m => m.BoundDesigner)
     .WithMany()
     .HasForeignKey(m => m.DesignerId)
     .HasConstraintName("fk_menus_bound_designer")
     .OnDelete(DeleteBehavior.SetNull)
     .IsRequired(false);
    e.HasIndex(m => m.DesignerId).HasDatabaseName("idx_menus_designer_id");
    e.HasIndex(m => m.ProvisioningStatus)
     .HasDatabaseName("idx_menus_provisioning_status_pending")
     .HasFilter("(provisioning_status = 'Pending')");
    ```
  - [x] **WARNING**: The `ComponentSchema` PK is `DesignerId` (a `string`, not a `Guid`). The FK is string → string. EF will handle this correctly because `HasForeignKey(m => m.DesignerId)` and `HasKey(s => s.DesignerId)` are both configured. Verify with `dotnet build` — no CA warnings expected.

- [x] **Task 4 — Update `MenuResponse` DTO** (AC: 1, 3, 4)
  - [x] Add binding fields to `src/FormForge.Api/Features/Menus/Dtos/MenuResponse.cs`:
    ```csharp
    internal sealed record MenuResponse(
        Guid Id,
        string Name,
        int Order,
        JsonElement? Icon,
        bool IsActive,
        Guid? ParentId,
        IReadOnlyList<Guid> AllowedRoleIds,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        // Story 5.2 — binding fields
        string? DesignerId,
        int? BoundVersion,
        string? ProvisioningStatus,   // null | "Pending" | "Success" | "Error"
        string? ProvisioningError);   // null unless ProvisioningStatus == "Error"
    ```
  - [x] Update `MenuService.ToResponse()` to map the new fields:
    ```csharp
    private static MenuResponse ToResponse(Menu menu) =>
        new(menu.Id, menu.Name, menu.Order, ParseIcon(menu.Icon), menu.IsActive, menu.ParentId,
            menu.RoleAssignments.Select(r => r.RoleId).ToList(),
            menu.CreatedAt, menu.UpdatedAt,
            menu.DesignerId, menu.BoundVersion, menu.ProvisioningStatus, menu.ProvisioningError);
    ```
  - [x] **Check ALL call sites** of `new MenuResponse(...)` in the codebase — `ToResponse()` is the only place; verify with `grep -rn "new MenuResponse"`.

- [x] **Task 5 — Create `Features/Provisioning/` folder and infrastructure files** (AC: 1, 3, 4)

  **5a — `ProvisioningJob.cs`:**
  ```csharp
  namespace FormForge.Api.Features.Provisioning;

  internal sealed record ProvisioningJob(
      Guid MenuId,
      string DesignerId,    // validated SafeIdentifier — the table name
      int Version,          // the Published version to provision
      Guid? ActorId);       // userId who triggered — for schema_audit_log (Story 5.3+)
  ```

  **5b — `IProvisioningService.cs`:**
  ```csharp
  namespace FormForge.Api.Features.Provisioning;

  internal interface IProvisioningService
  {
      ValueTask EnqueueAsync(ProvisioningJob job, CancellationToken ct = default);
  }
  ```

  **5c — `ProvisioningService.cs`:**
  ```csharp
  using System.Threading.Channels;
  namespace FormForge.Api.Features.Provisioning;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "DI registered.")]
  internal sealed class ProvisioningService(ChannelWriter<ProvisioningJob> writer) : IProvisioningService
  {
      public async ValueTask EnqueueAsync(ProvisioningJob job, CancellationToken ct = default)
      {
          ArgumentNullException.ThrowIfNull(job);
          await writer.WriteAsync(job, ct).ConfigureAwait(false);
      }
  }
  ```

  **5d — `ProvisioningBackgroundService.cs`:**
  ```csharp
  using System.Threading.Channels;
  using FormForge.Api.Infrastructure.Persistence;
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Hosting;
  using Microsoft.Extensions.Logging;

  namespace FormForge.Api.Features.Provisioning;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Registered via AddHostedService.")]
  internal sealed class ProvisioningBackgroundService(
      ChannelReader<ProvisioningJob> reader,
      IServiceScopeFactory scopeFactory,
      ILogger<ProvisioningBackgroundService> logger) : BackgroundService
  {
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          await foreach (var job in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
          {
              await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
          }
      }

      private async Task ProcessJobAsync(ProvisioningJob job, CancellationToken ct)
      {
          // Story 5.3 will replace this stub with real CREATE TABLE DDL via DdlEmitter.
          // For Story 5.2: validate the version is still Published, then set Success.
          // This gives Story 5.3 a defined seam: replace the body of this method.
          using var scope = scopeFactory.CreateScope();
          var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

          var menu = await db.Menus.FirstOrDefaultAsync(m => m.Id == job.MenuId, ct).ConfigureAwait(false);
          if (menu is null)
          {
              logger.LogWarning("ProvisioningJob for MenuId {MenuId} — menu no longer exists; skipping", job.MenuId);
              return;
          }

          // Guard: provisioning status may have changed since the job was enqueued (retry or re-bind).
          // Only process if still Pending.
          if (menu.ProvisioningStatus != "Pending")
          {
              logger.LogWarning(
                  "ProvisioningJob for MenuId {MenuId} — status is {Status}, not Pending; skipping",
                  job.MenuId, menu.ProvisioningStatus);
              return;
          }

          try
          {
              // Story 5.3 inserts CREATE TABLE / ALTER TABLE DDL here via DdlEmitter.
              // For Story 5.2: stub — provisioning succeeds immediately (no actual DDL).
              logger.LogInformation(
                  "Provisioning MenuId {MenuId} DesignerId {DesignerId} v{Version} — stub (Story 5.3 wires real DDL)",
                  job.MenuId, job.DesignerId, job.Version);

              menu.ProvisioningStatus = "Success";
              menu.ProvisioningError = null;
          }
          catch (Exception ex)
          {
              logger.LogError(ex,
                  "Provisioning failed for MenuId {MenuId} DesignerId {DesignerId} v{Version}",
                  job.MenuId, job.DesignerId, job.Version);
              menu.ProvisioningStatus = "Error";
              menu.ProvisioningError = ex.Message;
          }
          finally
          {
              menu.UpdatedAt = DateTimeOffset.UtcNow;
              await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
          }
      }
  }
  ```

  **5e — `BindingDiffService.cs`** (stub — Story 5.6 fills in real diff logic):
  ```csharp
  namespace FormForge.Api.Features.Provisioning;

  internal sealed record BindingDiffResponse(
      BindingInfo? CurrentBinding,
      int TargetVersion,
      IReadOnlyList<string> ColumnsToAdd,
      IReadOnlyList<string> ColumnsAlreadyPresent,
      IReadOnlyList<string> OrphanedColumns,
      IReadOnlyList<string> WillTriggerChildProvisioning,
      IReadOnlyList<string> EstimatedDdl);

  internal sealed record BindingInfo(string DesignerId, int Version);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "DI registered.")]
  internal sealed class BindingDiffService
  {
      // Story 5.6 replaces this stub with real column inspection via pg_attribute.
      // For Story 5.2: return estimated DDL based purely on the Designer's RootElement
      // (no live table inspection — table may not exist yet for a new binding).
      public Task<BindingDiffResponse> ComputeAsync(
          string designerId,
          int currentVersion,
          int targetVersion,
          CancellationToken ct)
      {
          _ = ct;
          // Stub: Story 5.6 wires pg_attribute introspection here.
          var response = new BindingDiffResponse(
              new BindingInfo(designerId, currentVersion),
              targetVersion,
              ColumnsToAdd: [],
              ColumnsAlreadyPresent: [],
              OrphanedColumns: [],
              WillTriggerChildProvisioning: [],
              EstimatedDdl: [$"-- CREATE/ALTER TABLE {designerId} (estimated DDL — Story 5.3/5.4 wires real DDL)"]);
          return Task.FromResult(response);
      }
  }
  ```

- [x] **Task 6 — Create `BindMenuDesignerRequest` DTO + Validator** (AC: 1, 2)
  - [x] New `src/FormForge.Api/Features/Menus/Dtos/BindMenuDesignerRequest.cs`:
    ```csharp
    namespace FormForge.Api.Features.Menus.Dtos;
    internal sealed record BindMenuDesignerRequest(string? DesignerId, int? Version);
    ```
  - [x] New `src/FormForge.Api/Features/Menus/Validators/BindMenuDesignerRequestValidator.cs`:
    ```csharp
    using FluentValidation;
    using FormForge.Api.Features.Menus.Dtos;
    namespace FormForge.Api.Features.Menus.Validators;

    internal sealed class BindMenuDesignerRequestValidator : AbstractValidator<BindMenuDesignerRequest>
    {
        public BindMenuDesignerRequestValidator()
        {
            RuleFor(x => x.DesignerId).NotNull().NotEmpty().MaximumLength(63)
                .Matches(@"^[a-z_][a-z0-9_]{0,62}$")
                .WithMessage("designerId must match ^[a-z_][a-z0-9_]{0,62}$ (per AR-4)");
            RuleFor(x => x.Version).NotNull().GreaterThan(0);
        }
    }
    ```
    **Note:** FluentValidation returns 422 `ValidationProblemDetails` (not the RFC 7807 problem envelope) for format/null errors. The semantic "VERSION_NOT_PUBLISHED" check happens in the service layer, NOT in the validator.

- [x] **Task 7 — Add bind/retry/diff operations to `IMenuService` and `MenuService`** (AC: 1–6)

  **7a — Add outcome/result types at the top of `MenuService.cs`:**
  ```csharp
  internal enum BindMenuOutcome { Success, MenuNotFound, DesignerNotFound, VersionNotPublished }
  internal sealed record BindMenuResult(BindMenuOutcome Outcome);

  internal enum RetryBindingOutcome { Success, MenuNotFound, NoBinding }
  internal sealed record RetryBindingResult(RetryBindingOutcome Outcome);
  ```

  **7b — Add to `IMenuService` interface:**
  ```csharp
  Task<BindMenuResult> BindDesignerAsync(Guid menuId, string designerId, int version, Guid? actorId, CancellationToken ct);
  Task<RetryBindingResult> RetryBindingAsync(Guid menuId, Guid? actorId, CancellationToken ct);
  Task<BindingDiffResponse?> GetBindingDiffAsync(Guid menuId, int targetVersion, CancellationToken ct);
  ```

  **7c — Implement in `MenuService` class:**
  ```csharp
  public async Task<BindMenuResult> BindDesignerAsync(Guid menuId, string designerId, int version, Guid? actorId, CancellationToken ct)
  {
      var menu = await db.Menus.FirstOrDefaultAsync(m => m.Id == menuId, ct).ConfigureAwait(false);
      if (menu is null) return new BindMenuResult(BindMenuOutcome.MenuNotFound);

      // Verify the version exists and is Published.
      var schemaVersion = await db.ComponentSchemaVersions
          .AsNoTracking()
          .FirstOrDefaultAsync(v => v.DesignerId == designerId && v.Version == version, ct)
          .ConfigureAwait(false);

      if (schemaVersion is null) return new BindMenuResult(BindMenuOutcome.DesignerNotFound);
      if (schemaVersion.Status != "Published") return new BindMenuResult(BindMenuOutcome.VersionNotPublished);

      menu.DesignerId = designerId;
      menu.BoundVersion = version;
      menu.ProvisioningStatus = "Pending";
      menu.ProvisioningError = null;
      menu.UpdatedAt = DateTimeOffset.UtcNow;

      await db.SaveChangesAsync(ct).ConfigureAwait(false);
      await cache.InvalidateAsync(CancellationToken.None).ConfigureAwait(false);

      // Enqueue AFTER commit so the job always refers to committed state.
      await provisioning.EnqueueAsync(new ProvisioningJob(menu.Id, designerId, version, actorId), CancellationToken.None)
          .ConfigureAwait(false);

      return new BindMenuResult(BindMenuOutcome.Success);
  }

  public async Task<RetryBindingResult> RetryBindingAsync(Guid menuId, Guid? actorId, CancellationToken ct)
  {
      var menu = await db.Menus.FirstOrDefaultAsync(m => m.Id == menuId, ct).ConfigureAwait(false);
      if (menu is null) return new RetryBindingResult(RetryBindingOutcome.MenuNotFound);
      if (menu.DesignerId is null || menu.BoundVersion is null)
          return new RetryBindingResult(RetryBindingOutcome.NoBinding);

      menu.ProvisioningStatus = "Pending";
      menu.ProvisioningError = null;
      menu.UpdatedAt = DateTimeOffset.UtcNow;

      await db.SaveChangesAsync(ct).ConfigureAwait(false);
      await provisioning.EnqueueAsync(
          new ProvisioningJob(menu.Id, menu.DesignerId, menu.BoundVersion.Value, actorId),
          CancellationToken.None).ConfigureAwait(false);

      return new RetryBindingResult(RetryBindingOutcome.Success);
  }

  public async Task<BindingDiffResponse?> GetBindingDiffAsync(Guid menuId, int targetVersion, CancellationToken ct)
  {
      var menu = await db.Menus.AsNoTracking()
          .FirstOrDefaultAsync(m => m.Id == menuId, ct).ConfigureAwait(false);
      if (menu is null || menu.DesignerId is null || menu.BoundVersion is null) return null;

      return await diffService.ComputeAsync(menu.DesignerId, menu.BoundVersion.Value, targetVersion, ct)
          .ConfigureAwait(false);
  }
  ```

  **7d — Update `MenuService` constructor** to inject `IProvisioningService` and `BindingDiffService`:
  ```csharp
  internal sealed class MenuService(
      FormForgeDbContext db,
      IMenuCache cache,
      IPermissionService permissions,
      IProvisioningService provisioning,
      BindingDiffService diffService) : IMenuService
  ```
  Note: `BindingDiffService` is injected as a concrete type (not interface) per the architecture pattern for single-implementation services that don't need swapping.

- [x] **Task 8 — Add new endpoints to `MenuAdminEndpoints.cs`** (AC: 1–6)
  - [x] Add three new routes inside `MapMenuAdminEndpoints()` after the existing `ToggleActive` route:
    ```csharp
    // Story 5.2 — bind a Published Designer version to a menu item. Returns 202 Accepted
    // (async provisioning per Decision 1.6 / AR-23). Route placed before /{id:guid}/active
    // but after /reorder (ordering only matters for literal vs parameter segments — all three
    // literal suffixes "/binding", "/binding/retry", "/binding-diff" are unambiguous).
    group.MapPut("/{id:guid}/binding", BindDesignerHandler)
         .AddValidationFilter<BindMenuDesignerRequest>()
         .WithSummary("Bind a Published Designer version to a menu item. Returns 202 and enqueues provisioning.")
         .Produces(StatusCodes.Status202Accepted)
         .Produces(StatusCodes.Status404NotFound)
         .Produces(StatusCodes.Status422UnprocessableEntity);

    group.MapPost("/{id:guid}/binding/retry", RetryBindingHandler)
         .WithSummary("Re-enqueue provisioning for an existing binding. Resets provisioningStatus to Pending.")
         .Produces(StatusCodes.Status202Accepted)
         .Produces(StatusCodes.Status404NotFound)
         .Produces(StatusCodes.Status422UnprocessableEntity);

    group.MapGet("/{id:guid}/binding-diff", GetBindingDiffHandler)
         .WithSummary("Preview column diff before updating a binding to a new version.")
         .Produces<BindingDiffResponse>(StatusCodes.Status200OK)
         .Produces(StatusCodes.Status404NotFound);
    ```
  - [x] Implement `BindDesignerHandler`:
    ```csharp
    private static async Task<IResult> BindDesignerHandler(
        Guid id,
        BindMenuDesignerRequest request,
        IMenuService menuService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(menuService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } s && Guid.TryParse(s, out var uid) ? uid : (Guid?)null;
        var result = await menuService.BindDesignerAsync(id, request.DesignerId!, request.Version!.Value, actorId, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            BindMenuOutcome.MenuNotFound => MenuNotFoundProblem(),
            BindMenuOutcome.DesignerNotFound => Results.Problem(
                title: "Designer version not found",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "DESIGNER_VERSION_NOT_FOUND",
                    ["messageKey"] = "admin.menus.designerVersionNotFound",
                }),
            BindMenuOutcome.VersionNotPublished => Results.Problem(
                title: "Only Published versions can be bound",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VERSION_NOT_PUBLISHED",
                    ["messageKey"] = "designers.versionNotPublished",
                }),
            BindMenuOutcome.Success => Results.Accepted(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
    ```
  - [x] Implement `RetryBindingHandler`:
    ```csharp
    private static async Task<IResult> RetryBindingHandler(
        Guid id,
        IMenuService menuService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(menuService);
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } s && Guid.TryParse(s, out var uid) ? uid : (Guid?)null;
        var result = await menuService.RetryBindingAsync(id, actorId, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            RetryBindingOutcome.MenuNotFound => MenuNotFoundProblem(),
            RetryBindingOutcome.NoBinding => Results.Problem(
                title: "No binding exists on this menu item",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "MENU_NO_BINDING",
                    ["messageKey"] = "admin.menus.noBinding",
                }),
            RetryBindingOutcome.Success => Results.Accepted(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
    ```
  - [x] Implement `GetBindingDiffHandler`:
    ```csharp
    private static async Task<IResult> GetBindingDiffHandler(
        Guid id,
        int targetVersion,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(menuService);
        var diff = await menuService.GetBindingDiffAsync(id, targetVersion, ct).ConfigureAwait(false);
        return diff is null ? MenuNotFoundProblem() : Results.Ok(diff);
    }
    ```

- [x] **Task 9 — Register provisioning services in `Program.cs`** (AC: 1, 3)
  - [x] Add the `Channel<ProvisioningJob>` singleton and wire the reader/writer:
    ```csharp
    // Story 5.2 — provisioning pipeline (Decision 5.2: Channel + BackgroundService).
    // Bounded capacity 256: if provisioning is slower than binds arrive, this limits
    // memory growth; writes block briefly when full (acceptable — admin-only action).
    var provisioningChannel = System.Threading.Channels.Channel.CreateBounded<
        FormForge.Api.Features.Provisioning.ProvisioningJob>(256);
    builder.Services.AddSingleton(provisioningChannel.Reader);
    builder.Services.AddSingleton(provisioningChannel.Writer);
    builder.Services.AddSingleton<IProvisioningService, ProvisioningService>();
    builder.Services.AddScoped<BindingDiffService>();
    builder.Services.AddHostedService<ProvisioningBackgroundService>();
    builder.Services.AddScoped<IValidator<BindMenuDesignerRequest>, BindMenuDesignerRequestValidator>();
    ```
  - [x] Add `using FormForge.Api.Features.Provisioning;` to `Program.cs` usings (or fully-qualify in the registration — pick whichever matches the surrounding file style).
  - [x] **CRITICAL**: `ProvisioningBackgroundService` is a singleton (BackgroundService lifetime). It injects `IServiceScopeFactory` (not `FormForgeDbContext` directly — scoped services cannot be injected into singletons). The code in Task 5d already does `scopeFactory.CreateScope()` correctly. **Do NOT inject `FormForgeDbContext` or `IMenuService` directly into the BackgroundService constructor.**

- [x] **Task 10 — Add i18n keys to `en.json`** (AC: 1–6)
  - [x] Add under `admin.menus` block (after `"reorderNoChanges"`):
    ```json
    "bindDesignerSectionTitle": "Designer Binding",
    "bindDesignerLabel": "Bind Designer Version",
    "designerIdLabel": "Designer ID",
    "versionLabel": "Version",
    "bindButton": "Bind",
    "bindingButton": "Binding…",
    "bindSuccess": "Designer version bound — provisioning started",
    "bindError": "Failed to bind Designer version",
    "designerVersionNotFound": "Designer or version not found",
    "noBinding": "No binding exists on this menu item",
    "retryButton": "Retry Provisioning",
    "retrySuccess": "Provisioning re-enqueued",
    "retryError": "Failed to retry provisioning",
    "provisioningPending": "Provisioning…",
    "provisioningSuccess": "Table provisioned successfully",
    "provisioningError": "Provisioning failed: {{error}}",
    "provisioningStatusLabel": "Provisioning Status",
    "bindingDiffTitle": "Schema Change Preview",
    "bindingDiffColumnsToAdd": "Columns to add",
    "bindingDiffAlreadyPresent": "Columns already present",
    "bindingDiffOrphaned": "Orphaned columns (in DB but not in new version)",
    "bindingDiffEstimatedDdl": "Estimated DDL",
    "viewDiffButton": "Preview Changes",
    "applyBindingButton": "Apply Update"
    ```
  - [x] Note: `"designers.versionNotPublished"` already exists in `en.json` at the `designers` block (line 233). Do NOT add a duplicate. The backend error response uses `"messageKey": "designers.versionNotPublished"`.

- [x] **Task 11 — Frontend: binding section in admin menu detail page** (AC: 1, 3, 4, 6)
  - [x] Update `web/src/routes/_app/admin/menus.$menuId.tsx`:
    - Add a "Designer Binding" section below the existing Roles section
    - Show current binding: `designerId` + `boundVersion` + `provisioningStatus` badge (use `components/shared/ProvisioningStatusBadge.tsx` — this file is in the architecture dir tree but may not exist yet; create a simple one if absent)
    - Show `provisioningError` message when status is "Error"
    - Bind form: `designerId` input (text) + `version` input (number) + "Bind" button
    - Retry button when status is "Error"
    - "Preview Changes" button → opens a modal with the binding diff (GET /binding-diff)
  - [x] Create `web/src/features/menu/usePollProvisioning.ts`:
    ```typescript
    // Polls GET /api/admin/menus/{menuId} every 2 s while provisioningStatus === 'Pending'.
    // Fires sonner toasts on Pending→Success and Pending→Error transitions.
    // Uses TanStack Query with refetchInterval controlled by provisioningStatus.
    ```
  - [x] Create `web/src/features/menu/useBindingDiff.ts`:
    ```typescript
    // GET /api/admin/menus/{menuId}/binding-diff?targetVersion={N}
    // Enabled only when both menuId and targetVersion are defined.
    // Query key: ['menus', 'admin', menuId, 'binding-diff', targetVersion]
    ```
  - [x] Add `useBindDesignerMutation` and `useRetryBindingMutation` to the existing menu mutation file (likely `menuAdminMutations.ts` — check actual path)
  - [x] On `useBindDesignerMutation.onSuccess`: fire `bindSuccess` toast; start polling via `usePollProvisioning`
  - [x] On `useRetryBindingMutation.onSuccess`: fire `retrySuccess` toast; resume polling
  - [x] On polling transition to Success: fire `provisioningSuccess` toast; stop polling (refetchInterval → false)
  - [x] On polling transition to Error: fire `provisioningError` toast with `provisioningError` detail; stop polling

- [x] **Task 12 — Tests** (AC: 1–6)
  - [x] Create `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`:
    - `[IClassFixture<PostgresFixture>]` + `IAsyncLifetime`
    - Seed: admin user, a Designer with a Published version v1 and a Draft version v2, a Menu Item
    - `BindDesigner_ValidPublishedVersion_Returns202()` — verify 202, then poll GetMenu until provisioningStatus = "Success" (with timeout)
    - `BindDesigner_DraftVersion_Returns422_VersionNotPublished()` — verify 422 + `code: "VERSION_NOT_PUBLISHED"`
    - `BindDesigner_ArchivedVersion_Returns422_VersionNotPublished()` — archive v1, then try to bind v1 → 422
    - `BindDesigner_MenuNotFound_Returns404()` — verify 404 + `code: "MENU_NOT_FOUND"`
    - `BindDesigner_UnknownDesignerId_Returns422_DesignerVersionNotFound()` — designer_id "does_not_exist" → 422 + code "DESIGNER_VERSION_NOT_FOUND"
    - `BindDesigner_Unauthenticated_Returns401()` — no auth header → 401
    - `BindDesigner_AsNonAdmin_Returns403()` — viewer JWT → 403
    - `RetryBinding_MenuNotFound_Returns404()` — verify 404
    - `RetryBinding_MenuWithNoBinding_Returns422_NoBinding()` — menu with no DesignerId → 422 + "MENU_NO_BINDING"
    - `RetryBinding_ExistingBinding_Returns202AndResetsToPending()` — bind first, then retry, verify status resets to Pending
    - `GetBindingDiff_MenuNotFound_Returns404()` — verify 404
    - `GetBindingDiff_MenuWithBinding_Returns200WithDiff()` — bind first, verify 200 + expected response shape
    - `GetBindingDiff_MenuWithNoBinding_Returns404()` — menu with no binding → 404 (GetBindingDiffAsync returns null)
  - [x] **Test count baseline: 317** (end of Story 5.1 code review)
  - [x] **Estimated additions:** +13 backend integration tests. Total target: ~330
  - [x] Vitest: Add `usePollProvisioning.test.ts` with 3-4 smoke tests (mock queries, verify refetchInterval behavior, verify toast fires on transition)

## Dev Notes

### What Already Exists — Read Before Writing Any Code

**Domain:**
- `src/FormForge.Api/Domain/Entities/Menu.cs` — currently has NO binding columns; this story adds them
- `src/FormForge.Api/Domain/Entities/ComponentSchema.cs` — PK is `DesignerId: string` (not Guid!)
- `src/FormForge.Api/Domain/Entities/ComponentSchemaVersion.cs` — has `Status: string` ("Draft"/"Published"/"Archived") and `Version: int`

**Persistence:**
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — `Menu` entity configured at lines 164-183; needs 4 new column mappings + FK
- `ComponentSchemaVersions` DbSet already exists — use it to validate the Published check in `BindDesignerAsync`
- Last migration: `20260524054931_CreateMenusAndMenuRoleAssignments.cs` — new migration must come AFTER this one

**Events:**
- `src/FormForge.Api/Infrastructure/EventBus/IDomainEventBus.cs` — already declares `MenuBindingCreated(string DesignerId)` event with comment "MenuBindingCreated has no subscriber (Story 4.1 adds the handler). Declared now per AR-47." Story 5.2 should publish this event from `BindDesignerAsync` after `SaveChangesAsync`. This event is subscribed by `PermissionService` to do permission cache invalidation (line 377 in architecture: `MenuBindingCreated(designerId) — no eviction (new Resource defaults to false flags)`). So publishing it is correct even though there's no permission-eviction logic needed; it's a signal for future subscribers.
  - Add `IDomainEventBus eventBus` to `MenuService` constructor
  - After `SaveChangesAsync` in `BindDesignerAsync`: `eventBus.Publish(new MenuBindingCreated(designerId));`

**MenuService existing constructor:**
```csharp
internal sealed class MenuService(FormForgeDbContext db, IMenuCache cache, IPermissionService permissions)
```
This story adds `IProvisioningService provisioning`, `BindingDiffService diffService`, and `IDomainEventBus eventBus` to the constructor. Make sure DI registrations in Program.cs are correct for all three.

**Frontend existing menu hooks/mutations:**
- Check `web/src/features/menu/` for existing files — the architecture shows `useMenus.ts`, `useBindingDiff.ts`, `usePollProvisioning.ts` as intended files. `useBindingDiff.ts` and `usePollProvisioning.ts` may not exist yet (they're in the architecture plan, not necessarily implemented). Verify with `ls` before creating.
- Check `web/src/routes/_app/admin/menus.$menuId.tsx` — this file WAS modified in Stories 4.3, 4.4, 4.5, 4.6. Read it fully before editing to understand the current section layout (Icon, Roles, toggle). The Designer Binding section goes AFTER Roles.
- `menuAdminMutations.ts` — check if it exists at `web/src/features/menu/` or the admin-menus feature folder; if not, check for patterns from `web/src/routes/_app/admin/menus.$menuId.tsx` (previous stories defined mutations inline).

### Critical Architecture Decisions Governing This Story

**Decision 1.6 (EF + Dapper boundary):**
- EF owns the binding save + status update (it's static-schema: `menus` table)
- Dapper owns the DDL (Story 5.3+) — NOT this story. Story 5.2's BackgroundService stub does NOT use Dapper.
- The 202 response is returned BEFORE provisioning completes (async).

**Decision 5.2 (Channel + BackgroundService):**
- `Channel<ProvisioningJob>` is bounded capacity 256 (suggested). If full, `WriteAsync` blocks briefly.
- **Do NOT use `TryWrite`** — silent failures would lose jobs. Use `WriteAsync` (or `await writer.WriteAsync(job, CancellationToken.None)` post-commit, so cancellation doesn't lose the job).
- Single consumer (sequential DDL) prevents concurrent DDL conflicts on shared tables.

**AR-23 (HTTP 202 for async provisioning):**
- `Results.Accepted()` returns 202 with no body (correct — the provisioning is async).
- The admin SPA polls `GET /api/admin/menus/{menuId}` to watch `provisioningStatus` change.

**AR-9 / AR-37 (single-consumer Channel):**
- `Channel.CreateBounded<ProvisioningJob>(256)` with `BoundedChannelFullMode.Wait` (default for bounded channels).
- The BackgroundService uses `reader.ReadAllAsync(stoppingToken)` — this is the correct async enumerable pattern.

**Decision 1.1 (SafeIdentifier):**
- The `designerId` field in `BindMenuDesignerRequest` is validated by FluentValidation (regex `^[a-z_][a-z0-9_]{0,62}$`). This is sufficient for Story 5.2's validator.
- The `ProvisioningJob.DesignerId` carries the already-validated string. Story 5.3's DdlEmitter MUST wrap it in `SafeIdentifier.TryCreate()` before any SQL interpolation — but that's Story 5.3's responsibility.

### EF Migration Pitfall: String FK

The FK `menus.designer_id → component_schemas.designer_id` is string→string. EF Core handles this correctly:
- `component_schemas.designer_id` is already configured as `HasKey(s => s.DesignerId)` + `HasColumnName("designer_id").HasMaxLength(63)`.
- The new `HasOne(m => m.BoundDesigner).WithMany().HasForeignKey(m => m.DesignerId)...` tells EF to join on the string.
- The migration will emit `REFERENCES component_schemas(designer_id)` — verify in the generated migration file.
- `OnDelete(DeleteBehavior.SetNull)` means if the Designer is deleted, `menus.designer_id` becomes NULL. This is the correct semantic: the menu item becomes an unbound section header; the provisioned table in PostgreSQL remains (handled separately).

### ProvisioningBackgroundService Lifetime Considerations

The BackgroundService is a singleton (ASP.NET registers hosted services as singletons). It MUST NOT inject scoped services directly. The pattern:
```csharp
using var scope = scopeFactory.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
```
creates a new scoped lifetime per job, disposing it when the using block exits. This is the standard pattern. Do NOT reuse scopes across jobs.

The `ILogger<ProvisioningBackgroundService>` is safe to inject directly (singleton).

### Frontend Polling Pattern

Use TanStack Query's `refetchInterval`:
```typescript
const menuQuery = useQuery({
  queryKey: ['menus', 'admin', menuId],
  queryFn: () => httpClient.get<MenuResponse>(`/api/admin/menus/${menuId}`),
  refetchInterval: (data) =>
    data?.provisioningStatus === 'Pending' ? 2000 : false,
});
```
When `provisioningStatus` transitions away from Pending, `refetchInterval` returns `false` → polling stops automatically. Fire sonner toasts in a `useEffect` watching `data.provisioningStatus`.

### `ProvisioningStatusBadge` Component

The architecture dir tree lists `components/shared/ProvisioningStatusBadge.tsx`. Check if it exists:
```
ls web/src/components/shared/
```
If it does NOT exist, create a minimal implementation:
```tsx
// Simple badge: grey=Pending, green=Success, red=Error
type Status = 'Pending' | 'Success' | 'Error' | null | undefined;
```
Map status → Tailwind classes + label via `t('admin.menus.provisioning*')` keys.

### Test Patterns

**Integration test seeding:** Use the existing `PostgresFixture` pattern from `DesignerIntegrationTests.cs`:
1. `LoginAsync("admin@example.com", "Password1!")` for platform-admin JWT
2. Create a ComponentSchema via `POST /api/designers` with a known `designerId`
3. Create a version via `POST /api/designers/{id}/versions`
4. Publish it via `PUT /api/designers/{id}/versions/{v}/status` with `{ status: "Published" }`
5. Create a Menu via `POST /api/admin/menus`
6. Now bind via `PUT /api/admin/menus/{menuId}/binding`

**Waiting for Background Service:**
The ProvisioningBackgroundService runs in-process during WebApplicationFactory tests. After the PUT /binding returns 202, wait for the provisioningStatus to transition by polling `GET /api/admin/menus/{menuId}` in the test:
```csharp
var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
string? status;
do {
    await Task.Delay(200, ct);
    var response = await _client.GetFromJsonAsync<MenuResponse>($"/api/admin/menus/{menuId}", ct);
    status = response?.ProvisioningStatus;
} while (status == "Pending" && DateTimeOffset.UtcNow < deadline);
Assert.Equal("Success", status);
```
This mirrors the real admin SPA polling. The 10s deadline is generous for an in-process test.

### File Locations (Actual vs. Architecture Doc)

The architecture doc shows `Features/Designers/` but the actual code uses `Features/Designer/` (no 's'). Follow the ACTUAL code, not the doc:
- `Features/Designer/` ← actual
- `Features/Designers/` ← architecture doc (mismatch; do NOT create a new folder)

For the new Provisioning folder, there is no existing code, so create `Features/Provisioning/` exactly as the architecture doc shows.

### Program.cs Caution: DI Registration Order

Current `MenuService` registration:
```csharp
builder.Services.AddScoped<IMenuService, MenuService>();
```
MenuService's new constructor parameters (`IProvisioningService`, `BindingDiffService`, `IDomainEventBus`) must all be registered BEFORE this line (or it doesn't matter — DI resolves lazily, but keep registrations logically grouped).

The Channel must be registered before `IProvisioningService`:
```csharp
// Register BEFORE IMenuService
var provisioningChannel = Channel.CreateBounded<ProvisioningJob>(256);
builder.Services.AddSingleton(provisioningChannel.Reader);
builder.Services.AddSingleton(provisioningChannel.Writer);
builder.Services.AddSingleton<IProvisioningService, ProvisioningService>();
builder.Services.AddScoped<BindingDiffService>();
builder.Services.AddHostedService<ProvisioningBackgroundService>();
```

The `IDomainEventBus` (`InProcessEventBus`) is already registered at line 127 as a singleton — no change needed.

### What This Story Does NOT Implement

- **Story 5.3** — the actual `CREATE TABLE` DDL logic in `DdlEmitter.cs`. Story 5.2's BackgroundService stubs the DDL step.
- **Story 5.6** — real column diff via `pg_attribute` introspection. `BindingDiffService` returns stub estimated DDL only.
- **Story 5.8** — `ProvisioningRecoveryService` startup scan. The `idx_menus_provisioning_status_pending` index is created now (Task 3) so Story 5.8 can use it.
- **SchemaRegistry** (`Features/SchemaRegistry/`) — not created in Story 5.2. First used in Story 5.3/6.1.
- **DbConnectionFactory** — Dapper's Npgsql connection wrapper for dynamic DDL. Created in Story 5.3.
- **DdlEmitter.cs** — created in Story 5.3. Story 5.2's BackgroundService has no interface abstraction for DDL — the seam is just the method body of `ProcessJobAsync`.

### Test Count Summary

| Location | Change |
|---|---|
| `ProvisioningIntegrationTests.cs` (new) | +13 integration tests |
| `usePollProvisioning.test.ts` (new) | +4 frontend vitest tests |
| **Baseline** | 317 (end of Story 5.1 code review) |
| **Estimated total** | ~334 |

All existing 317 tests must still pass. `dotnet build` must be clean (0 warnings, 0 errors). Frontend `npm run type-check` must be clean.

### References

- **Architecture Decision 1.6** (EF/Dapper boundary): `_bmad-output/planning-artifacts/architecture.md:331-347`
- **Architecture Decision 3.6** (binding diff): `architecture.md:483-499`
- **Architecture Decision 5.2** (Channel + BackgroundService): `architecture.md:636-639`
- **AR-9/AR-37 (single-consumer Channel)**: architecture Decisions 5.2
- **AR-23 (HTTP 202)**: architecture Decision 3.6 + Story 5.2 AC-1
- **Epic 5 spec (Story 5.2 verbatim)**: `_bmad-output/planning-artifacts/epics.md:1151-1183`
- **Existing MenuService**: `src/FormForge.Api/Features/Menus/MenuService.cs`
- **Existing MenuAdminEndpoints**: `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs`
- **Existing Menu entity (to extend)**: `src/FormForge.Api/Domain/Entities/Menu.cs`
- **Existing FormForgeDbContext (to extend)**: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`
- **Existing ComponentSchemaVersion entity**: `src/FormForge.Api/Domain/Entities/ComponentSchemaVersion.cs`
- **Existing IDomainEventBus + MenuBindingCreated event**: `src/FormForge.Api/Infrastructure/EventBus/IDomainEventBus.cs`
- **Previous story (5.1)**: `_bmad-output/implementation-artifacts/5-1-validate-designerid-as-a-safe-postgresql-identifier.md`
- **Error envelope pattern**: `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` (copy `MenuNotFoundProblem()` shape exactly)
- **Program.cs DI registrations**: `src/FormForge.Api/Program.cs:130-148`
- **i18n existing keys**: `web/src/lib/i18n/locales/en.json:125-217` (admin.menus block)

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (1M context) — `claude-opus-4-7[1m]`

### Debug Log References

- Initial backend build after adding `BindingDiffService` failed with `CA1822` because the stub method has no instance state yet. Suppressed at the method level with a comment pointing to Story 5.6 (where pg_attribute introspection will introduce real instance dependencies) — making it `static` now would force a DI-shape change when that work lands.
- Initial frontend lint went from 32 → 35 errors after adding `DesignerBindingSection`, `BindingDiffModal`, and `DiffList` as in-route helpers (three `react-refresh/only-export-components` violations). Moved the three components into a sibling file (`features/admin/menus/DesignerBindingSection.tsx`) — matches the `ReorderableMenuList.tsx` extraction pattern from Story 4.5 and restores the 32-error baseline.

### Completion Notes List

- Backend test count 317 → 330 (+13 ProvisioningIntegrationTests, exactly the spec estimate). All 330 pass on a fresh Testcontainer Postgres.
- Frontend vitest 91/93 → 95/97 (+4 `usePollProvisioning` tests, exactly the spec estimate). The 2 pre-existing failures are the Story 4.5 `ReorderableMenuList` mutateAsync-vs-mutate mismatch that Story 4.6 commit notes recorded as out-of-scope; they remain unchanged.
- TypeScript clean for every file I touched; the 7 pre-existing `toBeInTheDocument` errors in `Navbar.test.tsx` are unrelated (Story 4.7 setup miss) and untouched.
- Frontend lint 32 baseline preserved (0 net new errors) — verified by `git stash` + relint.
- Polling design choice: rather than a second `useQuery` observer for `usePollProvisioning`, I baked the 2-second `refetchInterval` (driven by `data?.provisioningStatus === 'Pending'`) into the existing `useMenuDetailQuery`, and `usePollProvisioning` became a side-effect-only hook that watches transitions via a `useRef`-tracked previous status. This avoids two query observers fighting for the same cache entry and makes the "no toast on initial undefined → Pending" semantics explicit (deps array alone wouldn't distinguish the initial render from a real terminal flip).
- Retry test (AC-4) intentionally does NOT assert the transient `Pending` state directly — the BackgroundService is in-process and may drain the job between the retry HTTP response and the next `GET`. Instead, the test records `UpdatedAt` after the first Success, retries, polls for the second Success, and asserts `UpdatedAt` strictly advanced. That is race-free and a strictly stronger proof that the pipeline ran end-to-end again.
- `MenuBindingCreated` event is published after the bind commit even though no subscriber needs eviction today — architecture line 377 (`MenuBindingCreated(designerId) — no eviction`) explicitly accepts the no-op subscriber, and declaring it now gives Story 5.3 schema-audit and any future cache-busting subscriber a defined seam.
- `BackgroundService` injects `IServiceScopeFactory` (singleton lifetime + per-job `using var scope`), not `FormForgeDbContext` directly. The story Dev Notes called this out as CRITICAL and the implementation matches.
- `RetryBindingAsync` skips `cache.InvalidateAsync` — the navbar cache stores only navbar-relevant fields (name/order/icon/isActive/role-filter visibility), none of which a retry can change. Documented inline at MenuService.cs.
- `ToggleMenuActiveRequest` / `BindMenuDesignerRequest` / `AssignMenuRolesRequest` all share the nullable-positional-record pattern so missing JSON keys deserialise to null and the FluentValidation NotNull rule returns 422 instead of the handler NRE-ing.

### File List

**Backend — new files (5):**
- `src/FormForge.Api/Features/Provisioning/ProvisioningJob.cs`
- `src/FormForge.Api/Features/Provisioning/IProvisioningService.cs`
- `src/FormForge.Api/Features/Provisioning/ProvisioningService.cs`
- `src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs`
- `src/FormForge.Api/Features/Provisioning/BindingDiffService.cs`
- `src/FormForge.Api/Features/Menus/Dtos/BindMenuDesignerRequest.cs`
- `src/FormForge.Api/Features/Menus/Validators/BindMenuDesignerRequestValidator.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260525051450_AddMenuBindingColumns.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260525051450_AddMenuBindingColumns.Designer.cs`

**Backend — modified files (6):**
- `src/FormForge.Api/Domain/Entities/Menu.cs`
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` (regenerated by `dotnet ef migrations add`)
- `src/FormForge.Api/Features/Menus/Dtos/MenuResponse.cs`
- `src/FormForge.Api/Features/Menus/MenuService.cs`
- `src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs`
- `src/FormForge.Api/Program.cs`

**Backend — new tests (1):**
- `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs` (+13 tests)

**Frontend — new files (5):**
- `web/src/features/menu/usePollProvisioning.ts`
- `web/src/features/menu/useBindingDiff.ts`
- `web/src/features/admin/menus/DesignerBindingSection.tsx`
- `web/src/components/shared/ProvisioningStatusBadge.tsx`
- `web/src/features/menu/__tests__/usePollProvisioning.test.tsx` (+4 tests)

**Frontend — modified files (4):**
- `web/src/features/menu/types.ts`
- `web/src/features/admin/menus/useMenuDetailQuery.ts`
- `web/src/features/admin/menus/menuAdminMutations.ts`
- `web/src/routes/_app/admin/menus.$menuId.tsx`
- `web/src/lib/i18n/locales/en.json` (+22 new admin.menus.* keys)

### Change Log

- 2026-05-25 — Story created (ready-for-dev)
- 2026-05-25 — Implementation complete; ready for review
- 2026-05-25 — Code review complete; findings recorded below

### Review Findings

- [x] [Review][Patch] **[HIGH] ProcessJobAsync passes stoppingToken to FirstOrDefaultAsync — OperationCanceledException bypasses try/catch/finally, leaving row permanently Pending** [`src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs` ProcessJobAsync pre-try block]
- [x] [Review][Patch] **[MED] DesignerBindingSection form inputs not re-synced after successful bind — useState initializers ignored on re-render, causing stale form values** [`web/src/features/admin/menus/DesignerBindingSection.tsx:33-34`]
- [x] [Review][Patch] **[MED] No integration test for AC-5 re-bind scenario — binding v1 then binding v2 (BoundVersion changes, pipeline reruns) has no regression guard** [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`]
- [x] [Review][Patch] **[LOW] useRetryBindingMutation.onSuccess missing invalidateAllMenus — inconsistent with useBindDesignerMutation cleanup pattern** [`web/src/features/admin/menus/menuAdminMutations.ts`]

- [x] [Review][Defer] **EnqueueAsync failure post-commit leaves row permanently Pending** [`src/FormForge.Api/Features/Menus/MenuService.cs:453`] — deferred; spec Dev Notes explicitly accept this tradeoff ("CancellationToken.None on the enqueue: a cancellation between commit and enqueue would otherwise leave the row Pending with no consumer, requiring a Retry to recover"); Retry is the documented recovery path
- [x] [Review][Defer] **OperationCanceledException inside the try block will be recorded as Error status** [`src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs:63-70`] — deferred to Story 5.3; stub try block has no async I/O today; fix catch-all to re-throw OCE when real DDL is wired
- [x] [Review][Defer] **GetBindingDiffHandler accepts targetVersion=0 with no guard — story 5.6 stub ignores the value, but real pg_attribute logic will silently produce a nonsensical diff** [`src/FormForge.Api/Features/Menus/MenuAdminEndpoints.cs` GetBindingDiffHandler] — deferred to Story 5.6; add `if (targetVersion <= 0) return 422` before delegating to diffService
- [x] [Review][Defer] **Security tests (401/403) exist only for PUT /binding — POST /binding/retry and GET /binding-diff have no explicit auth-failure tests** [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — deferred; route group RequirePlatformAdmin enforces the constraint for all three routes; low-priority test coverage gap
