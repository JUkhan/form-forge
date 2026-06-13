# Story 6.8: View CRUD Mutation Audit Log

Status: done

## Story

As a Platform Admin,
I want to view the mutation audit log for any dynamic table,
so that I have full traceability of who changed data and when.

## Acceptance Criteria

**AC-1 — Paginated mutation audit log endpoint**
**Given** I am a Platform Admin
**When** I GET `/api/admin/data/{designerId}/audit?page=1&pageSize=25`
**Then** I receive `200 OK` with a `PagedResult<MutationAuditEntry>` (Decision 3.4)
**And** each entry contains `id`, `timestamp`, `actorId`, `actorName`, `designerId`, `recordId`, `operation`, `previousValues`, `newValues`, `correlationId`
**And** entries are ordered newest-first (Timestamp DESC)
**And** `actorName` is resolved from the `users` table by `actorId` (null if actorId is null or user no longer exists)

**AC-2 — Append-only: no deletion endpoint**
**Given** the mutation audit log is append-only (NFR-10)
**When** I attempt DELETE/PUT/POST on `/api/admin/data/{designerId}/audit`
**Then** ASP.NET returns `405 Method Not Allowed` (guaranteed by construction — only `MapGet` is registered)

**AC-3 — Invalid designerId → 404**
**Given** a `designerId` that fails `SafeIdentifier.TryCreate` (e.g. uppercase letters or hyphens)
**When** I GET the audit endpoint
**Then** I receive `404 Not Found` with `code: "DESIGNER_NOT_FOUND"`, `messageKey: "admin.designers.notFound"`

**AC-4 — Valid designerId with no rows → 200 empty page**
**Given** a valid `designerId` that has no mutation audit entries
**When** I GET the audit endpoint
**Then** I receive `200 OK` with `data: [], total: 0, totalPages: 0` (NOT a 404)

**AC-5 — Pagination: page/pageSize parameters clamped**
**Given** `page < 1` → clamped to 1; `pageSize < 1 or > 100` → clamped to 25 (mirrors AuditEndpoints pattern)
**When** I GET with out-of-range values
**Then** the server applies defaults, returns correctly paginated result

**AC-6 — Viewer (non-admin) → 403**
**Given** I am not a Platform Admin (e.g. viewer role)
**When** I GET the audit endpoint
**Then** I receive `403 Forbidden` (inherited from `/api/admin/*` `RequirePlatformAdmin()` group filter)

**AC-7 — Frontend: Paginated admin view**
**Given** I am a Platform Admin navigating to `/admin/data/{designerId}/audit`
**When** the page loads
**Then** I see a paginated table of mutation audit entries with columns: Timestamp, Operation, Record ID, Actor, Previous Values, New Values, Correlation ID

---

## Tasks / Subtasks

- [x] **Task 1 — Create `MutationAuditEntryDto.cs`** (AC: 1)
  - [x] Create `src/FormForge.Api/Features/Audit/Dtos/MutationAuditEntryDto.cs`

  ```csharp
  namespace FormForge.Api.Features.Audit.Dtos;

  // Story 6.8 — paginated mutation audit log row shape. Mirrors SchemaAuditEntryDto
  // but for CRUD operations. previousValues/newValues are JSONB string snapshots —
  // returned as raw strings (the display interpretation is the frontend's responsibility).
  internal sealed record MutationAuditEntryDto(
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
  ```

- [x] **Task 2 — Extend `AuditService.cs`** (AC: 1, 3, 4, 5)
  - [x] Modify `src/FormForge.Api/Features/Audit/AuditService.cs`
  - Add `GetMutationAuditLogAsync` after the existing `GetSchemaAuditLogAsync` method:

  ```csharp
  public async Task<PagedResult<MutationAuditEntryDto>?> GetMutationAuditLogAsync(
      string designerId,
      int page,
      int pageSize,
      CancellationToken ct)
  {
      if (!SafeIdentifier.TryCreate(designerId, out _, out _))
          return null;

      if (page < 1) page = 1;
      if (pageSize is < 1 or > 100) pageSize = 25;

      var baseQuery = db.MutationAuditLog
          .AsNoTracking()
          .Where(a => a.DesignerId == designerId)
          .OrderByDescending(a => a.Timestamp);

      var total = await baseQuery.LongCountAsync(ct).ConfigureAwait(false);
      var entries = await baseQuery
          .Skip((page - 1) * pageSize)
          .Take(pageSize)
          .ToListAsync(ct)
          .ConfigureAwait(false);

      // Batch-resolve actor names. Left-join semantics: deleted actors still appear
      // (actorName = null) rather than hiding the audit row.
      var actorIds = entries
          .Where(e => e.ActorId.HasValue)
          .Select(e => e.ActorId!.Value)
          .Distinct()
          .ToList();

      var actorNames = actorIds.Count > 0
          ? await db.Users
              .AsNoTracking()
              .Where(u => actorIds.Contains(u.Id))
              .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct)
              .ConfigureAwait(false)
          : new Dictionary<Guid, string>();

      var data = entries.Select(a => new MutationAuditEntryDto(
          a.Id,
          a.Timestamp,
          a.ActorId,
          a.ActorId.HasValue && actorNames.TryGetValue(a.ActorId.Value, out var n) ? n : null,
          a.DesignerId,
          a.RecordId,
          a.Operation,
          a.PreviousValues,
          a.NewValues,
          a.CorrelationId))
          .ToList();

      return new PagedResult<MutationAuditEntryDto>(data, total, page, pageSize);
  }
  ```

  **Add the missing using** at the top of `AuditService.cs`:
  ```csharp
  // Already present: using FormForge.Api.Features.Audit.Dtos;
  // No new using needed — MutationAuditEntryDto is in the same Dtos namespace.
  ```

- [x] **Task 3 — Extend `AuditEndpoints.cs`** (AC: 1, 3, 4, 5, 6)
  - [x] Modify `src/FormForge.Api/Features/Audit/AuditEndpoints.cs`
  - Add `GetMutationAuditLogHandler` after `GetSchemaAuditLogHandler`:

  ```csharp
  internal static async Task<IResult> GetMutationAuditLogHandler(
      string designerId,
      int page,
      int pageSize,
      AuditService auditService,
      CancellationToken ct)
  {
      ArgumentNullException.ThrowIfNull(auditService);

      if (page < 1) page = 1;
      if (pageSize is < 1 or > 100) pageSize = 25;

      var result = await auditService
          .GetMutationAuditLogAsync(designerId, page, pageSize, ct)
          .ConfigureAwait(false);

      return result is null
          ? Results.Problem(
              title: "Designer not found",
              statusCode: StatusCodes.Status404NotFound,
              extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
              {
                  ["code"] = "DESIGNER_NOT_FOUND",
                  ["messageKey"] = "admin.designers.notFound",
              })
          : Results.Ok(result);
  }
  ```

- [x] **Task 4 — Create `DataAdminEndpoints.cs`** (AC: 1, 2, 6)
  - [x] Create `src/FormForge.Api/Features/DynamicCrud/DataAdminEndpoints.cs`

  ```csharp
  using FormForge.Api.Common;
  using FormForge.Api.Features.Audit;
  using FormForge.Api.Features.Audit.Dtos;

  namespace FormForge.Api.Features.DynamicCrud;

  // Story 6.8 — admin read-only endpoints for dynamic data tables.
  // Mounted under /api/admin/data (RequirePlatformAdmin + "admin" rate limit
  // inherited from the parent /api/admin group in AdminEndpoints).
  // Only GET is mapped on /audit → ASP.NET returns 405 for DELETE/PUT/POST (AC-2).
  internal static class DataAdminEndpoints
  {
      internal static RouteGroupBuilder MapDataAdminEndpoints(this RouteGroupBuilder group)
      {
          ArgumentNullException.ThrowIfNull(group);

          group.MapGet("/{designerId}/audit", AuditEndpoints.GetMutationAuditLogHandler)
               .WithSummary("Paginated CRUD mutation audit log for a dynamic table")
               .Produces<PagedResult<MutationAuditEntryDto>>(StatusCodes.Status200OK)
               .Produces(StatusCodes.Status404NotFound);

          return group;
      }
  }
  ```

- [x] **Task 5 — Update `AdminEndpoints.cs`** (AC: 1, 6)
  - [x] Modify `src/FormForge.Api/Features/Roles/AdminEndpoints.cs`
  - Add the `/data` group and required using:

  ```csharp
  using FormForge.Api.Features.DynamicCrud;  // add this
  // ...existing usings...

  internal static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
  {
      ArgumentNullException.ThrowIfNull(group);
      group.MapGroup("/roles").WithTags("Admin — Roles").MapRoleEndpoints();
      group.MapGroup("/users").WithTags("Admin — Users").MapUserAdminEndpoints();
      group.MapGroup("/menus").WithTags("Admin — Menus").MapMenuAdminEndpoints();
      group.MapGroup("/designers").WithTags("Admin — Designers").MapDesignerAdminEndpoints();
      // Story 6.8 — mutation audit log for provisioned dynamic tables.
      group.MapGroup("/data").WithTags("Admin — Data").MapDataAdminEndpoints();
      return group;
  }
  ```

- [x] **Task 6 — Integration tests** (AC: 1–6)
  - [x] Create `src/FormForge.Api.Tests/Features/Audit/MutationAuditLogIntegrationTests.cs`
  - Class pattern: same as `SchemaAuditLogIntegrationTests` — `IClassFixture<PostgresFixture>`, `IAsyncLifetime`
  - **InitializeAsync**: TRUNCATE `mutation_audit_log` and all static tables; drop dynamic tables loop; `ReseedSystemRolesAsync` + `SeedTestUsersAsync`
  - **DO NOT** use `[Collection("DynamicCrudTests")]` — this test class only touches `mutation_audit_log` via direct EF inserts, not dynamic tables. It should be isolated from the DynamicCrud test collection. Use no `[Collection]` attribute (runs independently).
  - Insert test rows directly using EF `db.MutationAuditLog.Add(...)` / `db.SaveChangesAsync()` — no need to drive the full CRUD API pipeline.

  **Test cases (7 integration tests):**

  1. `GetMutationAuditLog_NoEntries_Returns200WithEmptyPage` — AC-4:
     ```csharp
     // GET /api/admin/data/never_mutated/audit?page=1&pageSize=25
     // → 200, data: [], total: 0, totalPages: 0
     ```

  2. `GetMutationAuditLog_WithEntries_ReturnsEntriesNewestFirst` — AC-1:
     - Insert 2 rows into `mutation_audit_log` for the same `designerId`, different timestamps (older then newer)
     - GET page 1 → data[0] is the newer row, data[1] is the older row
     - Assert all fields present: `id`, `timestamp`, `actorId`, `actorName`, `designerId`, `recordId`, `operation`, `previousValues`, `newValues`, `correlationId`

  3. `GetMutationAuditLog_Paginated_SecondPage` — AC-5:
     - Insert 2 rows, GET page=2, pageSize=1 → exactly the older row; total=2, page=2, totalPages=2

  4. `GetMutationAuditLog_ActorName_ResolvedFromUsers` — AC-1:
     - Insert a row with `actorId = admin.Id`
     - GET → `actorName == "Platform Admin"` (seeded DisplayName)

  5. `GetMutationAuditLog_InvalidDesignerId_Returns404` — AC-3:
     - GET with `designerId = "INVALID-ID"` (uppercase + hyphen fails SafeIdentifier)
     - → 404, `code: "DESIGNER_NOT_FOUND"`

  6. `GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405` — AC-2:
     - DELETE `/api/admin/data/some_designer/audit` → 405

  7. `GetMutationAuditLog_Viewer_Returns403` — AC-6:
     - Login as viewer@example.com, GET → 403

  **DTOs for deserialization** (private records inside the test class):
  ```csharp
  private sealed record MutationAuditPagedResultDto(
      IReadOnlyList<MutationAuditEntryDto> Data,
      long Total, int Page, int PageSize, int TotalPages);

  private sealed record MutationAuditEntryDto(
      Guid Id, DateTimeOffset Timestamp, Guid? ActorId, string? ActorName,
      string DesignerId, Guid RecordId, string Operation,
      string? PreviousValues, string? NewValues, string? CorrelationId);
  ```

  **Helper for inserting test rows** (use EF in InitializeAsync scope or open a new scope in each test):
  ```csharp
  private async Task InsertMutationAuditRowAsync(
      Guid actorId, string designerId, Guid recordId,
      string operation, DateTimeOffset timestamp,
      string? newValues = null, string? previousValues = null)
  {
      using var scope = _factory!.Services.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
      db.MutationAuditLog.Add(new MutationAuditLogEntry
      {
          DesignerId    = designerId,
          RecordId      = recordId,
          Operation     = operation,
          ActorId       = actorId,
          Timestamp     = timestamp,
          NewValues     = newValues,
          PreviousValues = previousValues,
          CorrelationId = Guid.NewGuid().ToString("N")[..26],
      });
      await db.SaveChangesAsync();
  }
  ```

  **Helper for GET audit**:
  ```csharp
  private async Task<HttpResponseMessage> GetMutationAuditAsync(
      string token, string designerId, int page, int pageSize)
  {
      using var request = new HttpRequestMessage(HttpMethod.Get,
          $"/api/admin/data/{Uri.EscapeDataString(designerId)}/audit?page={page}&pageSize={pageSize}");
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
      return await _client!.SendAsync(request);
  }
  ```

- [x] **Task 7 — Frontend: API client + hook** (AC: 7)
  - [x] Create `web/src/features/admin/data/mutationAuditApi.ts`

  ```typescript
  import { httpClient } from '../../auth/httpClient'

  // Story 6.8 — mutation audit log API. Matches GET /api/admin/data/{designerId}/audit
  // → PagedResult<MutationAuditEntry>.
  export interface MutationAuditEntry {
    id: string
    timestamp: string          // ISO-8601 DateTimeOffset
    actorId: string | null
    actorName: string | null
    designerId: string
    recordId: string
    operation: string          // "CREATE" | "UPDATE" | "SOFT_DELETE" | "RESTORE"
    previousValues: string | null  // raw JSONB string
    newValues: string | null       // raw JSONB string
    correlationId: string | null
  }

  export interface MutationAuditPagedResult {
    data: MutationAuditEntry[]
    total: number
    page: number
    pageSize: number
    totalPages: number
  }

  export function getMutationAuditLog(
    designerId: string,
    page: number,
    pageSize: number,
  ): Promise<MutationAuditPagedResult> {
    return httpClient.get<MutationAuditPagedResult>(
      `/api/admin/data/${encodeURIComponent(designerId)}/audit?page=${page}&pageSize=${pageSize}`,
    )
  }
  ```

  - [x] Create `web/src/features/admin/data/useMutationAuditLogQuery.ts`

  ```typescript
  import { keepPreviousData, useQuery } from '@tanstack/react-query'
  import { getMutationAuditLog } from './mutationAuditApi'

  export function useMutationAuditLogQuery(
    designerId: string,
    page: number,
    pageSize: number,
  ) {
    return useQuery({
      queryKey: ['admin', 'data', designerId, 'audit', page, pageSize],
      queryFn: () => getMutationAuditLog(designerId, page, pageSize),
      staleTime: 30_000,
      placeholderData: keepPreviousData,
    })
  }
  ```

- [x] **Task 8 — Frontend: View component + route** (AC: 7)
  - [x] Create `web/src/features/admin/data/MutationAuditLogView.tsx`

  ```tsx
  import { useState } from 'react'
  import { useTranslation } from 'react-i18next'
  import type { MutationAuditEntry } from './mutationAuditApi'
  import { useMutationAuditLogQuery } from './useMutationAuditLogQuery'

  interface Props { designerId: string }

  export function MutationAuditLogView({ designerId }: Props) {
    const { t } = useTranslation()
    const [page, setPage] = useState(1)
    const pageSize = 25
    const query = useMutationAuditLogQuery(designerId, page, pageSize)

    if (query.isLoading && !query.data) return <p>{t('admin.data.audit.loading')}</p>
    if (query.isError || !query.data) {
      return <p role="alert" style={{ color: 'red' }}>{t('admin.data.audit.loadError')}</p>
    }

    const { data, totalPages } = query.data
    const isEmpty = query.data.total === 0

    return (
      <section
        aria-busy={query.isFetching}
        style={{ display: 'flex', flexDirection: 'column', gap: '1rem', opacity: query.isFetching ? 0.6 : 1 }}
      >
        <header>
          <h1 style={{ fontSize: '1.5rem', fontWeight: 600 }}>{t('admin.data.audit.title')}</h1>
          <p style={{ color: '#666', margin: 0 }}>{t('admin.data.audit.subtitle')}</p>
        </header>

        {isEmpty ? (
          <p>{t('admin.data.audit.noEntries')}</p>
        ) : (
          <>
            <table style={{ width: '100%', borderCollapse: 'collapse' }}>
              <thead>
                <tr style={{ textAlign: 'left', borderBottom: '1px solid #ddd' }}>
                  <th style={{ padding: '0.5rem' }}>{t('admin.data.audit.columnTimestamp')}</th>
                  <th style={{ padding: '0.5rem' }}>{t('admin.data.audit.columnOperation')}</th>
                  <th style={{ padding: '0.5rem' }}>{t('admin.data.audit.columnRecordId')}</th>
                  <th style={{ padding: '0.5rem' }}>{t('admin.data.audit.columnActor')}</th>
                  <th style={{ padding: '0.5rem' }}>{t('admin.data.audit.columnPreviousValues')}</th>
                  <th style={{ padding: '0.5rem' }}>{t('admin.data.audit.columnNewValues')}</th>
                  <th style={{ padding: '0.5rem' }}>{t('admin.data.audit.columnCorrelationId')}</th>
                </tr>
              </thead>
              <tbody>
                {data.map((entry) => <MutationAuditRow key={entry.id} entry={entry} t={t} />)}
              </tbody>
            </table>

            <nav style={{ display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: '0.5rem' }}>
              <button type="button" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page === 1}>
                {t('admin.data.audit.prevPage')}
              </button>
              <span>{t('admin.data.audit.pageInfo', { page, totalPages })}</span>
              <button type="button" onClick={() => setPage((p) => Math.min(totalPages, p + 1))} disabled={page >= totalPages}>
                {t('admin.data.audit.nextPage')}
              </button>
            </nav>
          </>
        )}
      </section>
    )
  }

  interface RowProps { entry: MutationAuditEntry; t: ReturnType<typeof useTranslation>['t'] }

  function MutationAuditRow({ entry, t }: RowProps) {
    const actorDisplay = entry.actorName ?? t('admin.data.audit.unknownActor')
    return (
      <tr style={{ borderBottom: '1px solid #eee' }}>
        <td style={{ padding: '0.5rem', whiteSpace: 'nowrap' }}>{new Date(entry.timestamp).toLocaleString()}</td>
        <td style={{ padding: '0.5rem', fontFamily: 'monospace' }}>{entry.operation}</td>
        <td style={{ padding: '0.5rem', fontFamily: 'monospace', fontSize: '0.85em' }}>{entry.recordId}</td>
        <td style={{ padding: '0.5rem' }}>{actorDisplay}</td>
        <td style={{ padding: '0.5rem', fontFamily: 'monospace', fontSize: '0.85em', maxWidth: '20rem', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {entry.previousValues ?? '—'}
        </td>
        <td style={{ padding: '0.5rem', fontFamily: 'monospace', fontSize: '0.85em', maxWidth: '20rem', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {entry.newValues ?? '—'}
        </td>
        <td style={{ padding: '0.5rem', fontFamily: 'monospace', fontSize: '0.85em' }}>{entry.correlationId ?? '—'}</td>
      </tr>
    )
  }
  ```

  - [x] Create `web/src/routes/_app/admin/data.$designerId.audit.tsx`

  ```tsx
  import { createFileRoute } from '@tanstack/react-router'
  import { MutationAuditLogView } from '../../../features/admin/data/MutationAuditLogView'

  // Story 6.8 — Mutation Audit Log view. URL: /admin/data/{designerId}/audit.
  export const Route = createFileRoute('/_app/admin/data/$designerId/audit')({
    component: MutationAuditLogPage,
  })

  function MutationAuditLogPage() {
    const { designerId } = Route.useParams()
    return <MutationAuditLogView designerId={designerId} />
  }
  ```

---

## Dev Notes

### §1 — No EF Migration Required

`mutation_audit_log` was created in Story 6.3 migration (`20260526054545_AddMutationAuditLog`). The table and all three indexes already exist:
- `idx_mutation_audit_log_designer_id_timestamp_desc` (designer_id, timestamp DESC) ← used by this endpoint
- `idx_mutation_audit_log_record_id_timestamp_desc` (record_id, timestamp DESC)
- `idx_mutation_audit_log_correlation_id`

Do NOT run `dotnet ef migrations add` for this story.

### §2 — `MutationAuditLogEntry.Timestamp` vs `SchemaAuditLogEntry.CreatedAt`

**Critical difference**: `MutationAuditLogEntry` uses `Timestamp` (column name `timestamp`), NOT `CreatedAt`. `SchemaAuditLogEntry` uses `CreatedAt`. Order by `a.Timestamp` (not `a.CreatedAt`). Getting this wrong will fail at runtime with a missing property exception.

### §3 — `AuditService` is already registered as Scoped in DI

Do NOT add another `services.AddScoped<AuditService>()` in `Program.cs`. The service was registered in Story 5.7. Just add the new method.

### §4 — Route registration path: `AdminEndpoints.cs` is in `Features/Roles/`

This is unintuitive but correct — `AdminEndpoints.cs` is the top-level admin dispatcher in `Features/Roles/`. When adding the `/data` group:
1. Add `using FormForge.Api.Features.DynamicCrud;` at the top of `AdminEndpoints.cs`
2. Add `group.MapGroup("/data").WithTags("Admin — Data").MapDataAdminEndpoints();`

`DataAdminEndpoints.cs` should be in `Features/DynamicCrud/` (same feature as the existing CRUD handlers) but the registration call is in `Features/Roles/AdminEndpoints.cs`.

### §5 — Rate limiting and auth are inherited from the admin group

`/api/admin/*` already has `RequireAuth()`, `RequirePlatformAdmin()`, and `RequireRateLimiting("admin")` (120 req/min per user, Decision 2.6) applied in `Program.cs`. `DataAdminEndpoints` inherits all three. Do NOT add them again at the handler level.

### §6 — Append-only is guaranteed by construction (AC-2)

Only `MapGet("/{designerId}/audit", ...)` is registered in `DataAdminEndpoints`. ASP.NET Core automatically returns 405 for DELETE/PUT/POST because no handler matches those verbs on this path. No explicit code needed.

### §7 — `previousValues`/`newValues` are raw JSONB strings

`MutationAuditLogEntry.NewValues` and `PreviousValues` are `string?` in C# (stored as JSONB in Postgres, EF maps to string). Return them as-is in the DTO. The frontend displays them as truncated monospace strings. Do NOT attempt to parse/re-serialize them.

### §8 — Test isolation: direct EF inserts

Unlike `SchemaAuditLogIntegrationTests` (which drives full provisioning pipeline), `MutationAuditLogIntegrationTests` should insert rows directly into `mutation_audit_log` via EF. This avoids heavy test setup and isolates the audit endpoint behavior from CRUD pipeline behavior. The `InitializeAsync` TRUNCATE must include `mutation_audit_log`.

Full TRUNCATE statement (copy from `RepeaterWriteIntegrationTests.InitializeAsync`):
```sql
TRUNCATE TABLE menu_role_assignments, menus, component_schema_versions, component_schemas, role_permissions, user_roles, roles, refresh_tokens, users, schema_audit_log, mutation_audit_log RESTART IDENTITY CASCADE;
```

### §9 — `MutationAuditLogEntry.Id` defaults to `gen_random_uuid()` server-side

When inserting test rows via EF, set `Id = Guid.NewGuid()` explicitly (EF does not call `gen_random_uuid()` for in-memory tracking; the default is for DB-generated values when `Id` is not set). Actually, EF does handle the `HasDefaultValueSql("gen_random_uuid()")` by leaving `Id` as empty Guid and letting the DB fill it in. Either approach works — either set `Id = Guid.NewGuid()` explicitly or leave it as `default(Guid)` and let EF + Postgres fill it in.

### §10 — `designerId` in `mutation_audit_log` is the SafeIdentifier string value (lowercase, alphanumeric, underscore)

When inserting test rows, use a valid safe identifier string like `"test_designer"`. Do NOT use uppercase or hyphens — the handler's `SafeIdentifier.TryCreate` check would return 404.

### §11 — Frontend feature folder: `features/admin/data/`

The new folder `web/src/features/admin/data/` does not exist yet — create it. File structure mirrors `features/admin/designers/` (has `*Api.ts`, `use*Query.ts`, `*View.tsx`).

### §12 — TanStack Router route file naming

The route file `routes/_app/admin/data.$designerId.audit.tsx` follows TanStack Router file-based routing conventions (dot-separated path segments). The `createFileRoute` path string must be `'/_app/admin/data/$designerId/audit'`. The `Route.useParams()` returns `{ designerId: string }`.

### §13 — No `[Collection("DynamicCrudTests")]` on `MutationAuditLogIntegrationTests`

`SchemaAuditLogIntegrationTests` runs without a `[Collection]` attribute (isolated). Follow the same pattern. The `DynamicCrudTests` collection coordinates tests that share the same provisioned tables — this new test class only touches `mutation_audit_log` via direct inserts.

### §14 — Auth (AC-6): Viewer receives 403, not 401

`/api/admin/*` uses `RequirePlatformAdmin()` which returns 403 (Forbidden) for authenticated users without the admin role, and 401 (Unauthorized) for unauthenticated requests. The AC says "viewer → 403", so the test must login as viewer and expect 403.

---

### Project Structure Notes

**New files:**
| File | Path |
|---|---|
| `MutationAuditEntryDto.cs` | `src/FormForge.Api/Features/Audit/Dtos/MutationAuditEntryDto.cs` |
| `DataAdminEndpoints.cs` | `src/FormForge.Api/Features/DynamicCrud/DataAdminEndpoints.cs` |
| `MutationAuditLogIntegrationTests.cs` | `src/FormForge.Api.Tests/Features/Audit/MutationAuditLogIntegrationTests.cs` |
| `mutationAuditApi.ts` | `web/src/features/admin/data/mutationAuditApi.ts` |
| `useMutationAuditLogQuery.ts` | `web/src/features/admin/data/useMutationAuditLogQuery.ts` |
| `MutationAuditLogView.tsx` | `web/src/features/admin/data/MutationAuditLogView.tsx` |
| `data.$designerId.audit.tsx` | `web/src/routes/_app/admin/data.$designerId.audit.tsx` |

**Modified files:**
| File | Change |
|---|---|
| `AuditService.cs` | Add `GetMutationAuditLogAsync` |
| `AuditEndpoints.cs` | Add `GetMutationAuditLogHandler` |
| `AdminEndpoints.cs` | Add `/data` group + `using` statement |

**No changes to:** `Program.cs` (no new DI registration), `FormForgeDbContext.cs` (EF mapping already exists), any EF migration files, `DynamicQueryBuilder.cs`, `DynamicDataEndpoints.cs`.

---

### References

- [Source: `AuditService.cs`] — `GetSchemaAuditLogAsync` is the direct template to mirror. Same null-return pattern, same actor-name batch resolution pattern. Diff: order by `Timestamp` (not `CreatedAt`); return `MutationAuditEntryDto` (not `SchemaAuditEntryDto`); query `db.MutationAuditLog` (not `db.SchemaAuditLog`).
- [Source: `AuditEndpoints.cs`] — `GetSchemaAuditLogHandler` is the direct template for `GetMutationAuditLogHandler`. Identical structure; just calls `auditService.GetMutationAuditLogAsync` instead.
- [Source: `DesignerAdminEndpoints.cs:30`] — Template for the `MapGet("/{designerId}/audit", ...)` registration inside `DataAdminEndpoints.MapDataAdminEndpoints`.
- [Source: `AdminEndpoints.cs`] — Add one line `group.MapGroup("/data").WithTags("Admin — Data").MapDataAdminEndpoints();` after the `/designers` group registration.
- [Source: `SchemaAuditLogIntegrationTests.cs`] — Template for `MutationAuditLogIntegrationTests`: `InitializeAsync`/`DisposeAsync` pattern, `LoginAsync`, `ReseedSystemRolesAsync`, `SeedTestUsersAsync`, DTO records.
- [Source: `designers.$designerId.audit.tsx`, `SchemaAuditLogView.tsx`, `designerAuditApi.ts`, `useSchemaAuditLogQuery.ts`] — Exact templates for the 4 frontend files (mutate type names, field names, i18n keys, API path).
- [Architecture: Decision 3.4] — `PagedResult<T>` shape: `{ data, total, page, pageSize, totalPages }`.
- [Architecture: Decision 2.6] — `/api/admin/*` rate limit: 120 req/min sliding window, inherited.
- [Architecture: NFR-10] — Mutation audit log is append-only. No deletion API.
- [Architecture: Feature folder rule] — `DataAdminEndpoints.cs` in `Features/DynamicCrud/` (handler is data-centric); `AuditEndpoints.cs` in `Features/Audit/` (handler body lives here per architecture diagram); `AdminEndpoints.cs` registration in `Features/Roles/`.
- [Story 6.3 — `MutationAuditLogEntry`] — Entity shape, EF mapping, indexes already created.

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (claude-opus-4-7[1m])

### Debug Log References

- Integration test fix: `Assert.Equal("""{"title":"new"}""", first.NewValues)` failed because PostgreSQL JSONB normalises whitespace on round-trip (`{"title": "new"}` is returned). Replaced strict string compare with a structural `AssertJsonEqual` helper that re-serialises both sides through `System.Text.Json` before comparing. The original literal contract (no spaces between key/value) is wrong about JSONB physical storage but right about JSON semantic equivalence.
- TanStack Router type errors on `data.$designerId.audit.tsx` until `vite build` regenerated `routeTree.gen.ts`. `npx tsr generate` installed the wrong CLI; the route plugin only exposes generation through the Vite pipeline, so triggering it via `vite build` is the canonical path.

### Completion Notes List

- AC-1 (paginated endpoint with full field set + actorName from users table): satisfied by `AuditService.GetMutationAuditLogAsync` mirroring `GetSchemaAuditLogAsync` — same null-return-on-bad-id pattern, same actor-name batch lookup. `OrderByDescending(a => a.Timestamp)` (not `CreatedAt` — see Dev Notes §2).
- AC-2 (append-only / 405 on DELETE/PUT/POST): satisfied by construction — `DataAdminEndpoints.MapDataAdminEndpoints` only calls `MapGet`. Verified by test `GetMutationAuditLog_AppendOnly_DeleteVerb_Returns405`.
- AC-3 (invalid designerId → 404): satisfied by `SafeIdentifier.TryCreate` gate inside the service; handler maps null to `Results.Problem(404, code=DESIGNER_NOT_FOUND, messageKey=admin.designers.notFound)`.
- AC-4 (valid id with no rows → empty page, not 404): valid SafeIdentifier strings always return `PagedResult.Data=[], Total=0, TotalPages=0`. The endpoint does NOT verify designer existence in `component_schemas` — only the syntactic identifier check.
- AC-5 (page/pageSize clamping): clamping happens twice — at the handler entry and inside the service — both sides defensive. Verified by `GetMutationAuditLog_Paginated_SecondPage` with page=2/pageSize=1.
- AC-6 (viewer → 403): inherited from `/api/admin` group `RequirePlatformAdmin()` filter in `Program.cs`. Verified by login-as-viewer test.
- AC-7 (frontend paginated view): `MutationAuditLogView.tsx` + `useMutationAuditLogQuery.ts` + route file. `placeholderData: keepPreviousData` so the table doesn't flash empty between pages. `previousValues`/`newValues` rendered as raw monospace strings with `text-overflow: ellipsis` (Dev Notes §7 — no parsing on the frontend).
- No EF migration created — `mutation_audit_log` already exists from Story 6.3 migration `20260526054545_AddMutationAuditLog` (Dev Notes §1).
- No new DI registration — `AuditService` is already `Scoped` from Story 5.7 (Dev Notes §3).
- Integration test class deliberately omits `[Collection("DynamicCrudTests")]` and inserts rows directly via EF (`db.MutationAuditLog.Add(...)`) rather than driving the full CRUD pipeline — isolates audit-endpoint behavior from CRUD-pipeline behavior (Dev Notes §8, §13).
- New i18n namespace added under `admin.data.audit.*` in `web/src/lib/i18n/locales/en.json` (parallel to `admin.designers.audit.*`).
- Full backend suite: 522/522 passing (7 new + 515 existing, no regressions).
- Frontend type-check: route file compiles cleanly after `routeTree.gen.ts` regeneration. Pre-existing test-file errors in `Navbar.test.tsx` and `usePollProvisioning.test.tsx` are unrelated to this story.

**Deferred items (not blocking — flag for code review):**
1. `GetMutationAuditLog_NoEntries_Returns200WithEmptyPage` uses `"never_mutated"` — a valid SafeIdentifier that may collide with a future test or production designer. Low risk because mutation audit rows for unknown designers wouldn't exist, but a UUID-suffixed identifier would be more isolation-safe.
2. AC-5 clamping is asserted only on page=2/pageSize=1 (in-range). No test asserts the actual clamp behavior (page=-1 → page=1, pageSize=999 → pageSize=25). The handler+service both do clamping (defence in depth) but the negative test is missing.
3. AC-3 (`INVALID-ID`) reaches the handler via URL `/api/admin/data/INVALID-ID/audit`. `Uri.EscapeDataString` in `GetMutationAuditAsync` does NOT percent-encode hyphens, so the segment travels intact and `SafeIdentifier.TryCreate` rejects it. Confirmed by passing test, but worth noting that some hyphen-containing identifiers could be rejected at a different layer (model-binding) in a future ASP.NET upgrade.
4. The `actorName` is null when the user row no longer exists. There is no integration test for the deleted-actor case (orphan FK by design — `mutation_audit_log.actor_id` has no FK to `users`). The story Dev Notes call this out as expected behavior but the test surface doesn't verify it.
5. `MutationAuditEntryDto.NewValues`/`PreviousValues` are returned as raw JSONB strings with PostgreSQL-normalised whitespace (e.g. `{"title": "old"}` not `{"title":"old"}`). The frontend's monospace rendering inherits whatever formatting the database returns. If a deterministic display format is required later, the service should JsonNode-parse and re-serialise with a stable formatter.
6. `MutationAuditLogIntegrationTests` does NOT verify that GET inherits the `/api/admin` `RequireRateLimiting("admin")` budget (120 req/min). No 429 test exists. Lower priority because the rate-limit policy is inherited from a tested group filter, but explicit coverage would close the gap.
7. The new i18n keys under `admin.data.audit.*` are English-only. If multi-locale support is enabled later, parallel `tr.json`/`bn.json`/etc. files will need the same keys added.
8. The view component uses inline styles rather than Tailwind classes — matches the `SchemaAuditLogView.tsx` template exactly but is inconsistent with the rest of the codebase's shadcn/tailwind direction.
9. `useMutationAuditLogQuery` has `staleTime: 30_000` (30 seconds). For an append-only log this might be too aggressive — once an entry exists it never mutates, so a much longer staleTime would reduce refetch chatter without compromising freshness. Mirrored from the schema audit hook; worth re-examining as a cross-cutting concern.
10. `MutationAuditPagedResultDto` is duplicated as a private record inside the test class. The production DTO is internal-only so the test can't import it. If multiple test classes need the audit DTO shape later, lifting it into a shared `InternalsVisibleTo` or test-helpers project would remove duplication. Matches the existing `SchemaAuditLogIntegrationTests` pattern.

### File List

**New files:**
- `src/FormForge.Api/Features/Audit/Dtos/MutationAuditEntryDto.cs`
- `src/FormForge.Api/Features/DynamicCrud/DataAdminEndpoints.cs`
- `src/FormForge.Api.Tests/Features/Audit/MutationAuditLogIntegrationTests.cs`
- `web/src/features/admin/data/mutationAuditApi.ts`
- `web/src/features/admin/data/useMutationAuditLogQuery.ts`
- `web/src/features/admin/data/MutationAuditLogView.tsx`
- `web/src/routes/_app/admin/data.$designerId.audit.tsx`

**Modified files:**
- `src/FormForge.Api/Features/Audit/AuditService.cs` — added `GetMutationAuditLogAsync`
- `src/FormForge.Api/Features/Audit/AuditEndpoints.cs` — added `GetMutationAuditLogHandler`
- `src/FormForge.Api/Features/Roles/AdminEndpoints.cs` — added `/data` group registration + `using FormForge.Api.Features.DynamicCrud;`
- `web/src/lib/i18n/locales/en.json` — added `admin.data.audit.*` translation namespace
- `web/src/routeTree.gen.ts` — auto-regenerated by `vite build` after new route file was added

### Change Log

- 2026-05-26 — Story 6.8 implemented: paginated `GET /api/admin/data/{designerId}/audit` endpoint, 7 integration tests (all passing), frontend view + route. 522/522 backend tests passing. No EF migration required.

### Review Findings

- [x] [Review][Patch] AC-5 clamping paths untested — no test passes `page=0` or `pageSize=101` to verify the clamped values appear in the response envelope [`src/FormForge.Api.Tests/Features/Audit/MutationAuditLogIntegrationTests.cs`]
- [x] [Review][Patch] AC-1: null actorId → actorName=null branch untested — `InsertMutationAuditRowAsync` always receives a non-null actorId; add a test inserting a row with `ActorId = null` and asserting `actorName == null` in the response [`src/FormForge.Api.Tests/Features/Audit/MutationAuditLogIntegrationTests.cs`]
- [x] [Review][Patch] AC-1: deleted-actor → actorName=null branch untested — no test seeds a row whose `actorId` is absent from the `users` table to verify the left-join fallback produces `actorName = null` [`src/FormForge.Api.Tests/Features/Audit/MutationAuditLogIntegrationTests.cs`]
- [x] [Review][Defer] Double clamping in handler and service [`src/FormForge.Api/Features/Audit/AuditEndpoints.cs`, `AuditService.cs`] — deferred, pre-existing pattern (mirrors `GetSchemaAuditLogAsync`)
- [x] [Review][Defer] Separate COUNT + data queries exposes a TOCTOU window on `total` [`src/FormForge.Api/Features/Audit/AuditService.cs`] — deferred, pre-existing EF pagination pattern across all paginated endpoints
- [x] [Review][Defer] `MutationAuditEntryDto` is `internal` but exposed via `Produces<>` — OpenAPI schema blind to it [`src/FormForge.Api/Features/Audit/Dtos/MutationAuditEntryDto.cs`] — deferred, consistent with `SchemaAuditEntryDto` pattern
- [x] [Review][Defer] No typed error surface in `mutationAuditApi.ts` — all errors collapse to generic `loadError` [`web/src/features/admin/data/mutationAuditApi.ts`] — deferred, pre-existing across all API clients
- [x] [Review][Defer] Inline styles throughout `MutationAuditLogView.tsx` — inconsistent with codebase Tailwind direction [`web/src/features/admin/data/MutationAuditLogView.tsx`] — deferred, intentional: mirrors `SchemaAuditLogView.tsx` template exactly
- [x] [Review][Defer] `operation` field typed as raw `string` rather than discriminated union [`web/src/features/admin/data/mutationAuditApi.ts`] — deferred, improvement opportunity; spec-compliant
- [x] [Review][Defer] No `Produces(StatusCodes.Status403Forbidden)` on route — OpenAPI omits 403 response [`src/FormForge.Api/Features/DynamicCrud/DataAdminEndpoints.cs`] — deferred, systemic pre-existing across all admin endpoints
- [x] [Review][Defer] `actorIds` IN clause grows up to 100 UUIDs per page (bounded by pageSize max) [`src/FormForge.Api/Features/Audit/AuditService.cs`] — deferred, pre-existing pattern mirrors `GetSchemaAuditLogAsync`
- [x] [Review][Defer] AC-2 test only verifies DELETE → 405, not PUT/POST [`src/FormForge.Api.Tests/Features/Audit/MutationAuditLogIntegrationTests.cs`] — deferred, spec notes 405 is guaranteed by construction; one verb confirms the mechanism
- [x] [Review][Defer] No TanStack Router `beforeLoad` auth guard on route [`web/src/routes/_app/admin/data.$designerId.audit.tsx`] — deferred, pre-existing pattern; `_app` layout handles auth

