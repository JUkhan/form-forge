# Story 5.3: Provision New Table from Designer Schema

Status: done

## Story

As the system,
I `CREATE TABLE` when a Designer is bound to a Menu Item for the first time,
So that a real PostgreSQL table backs the data module.

## Acceptance Criteria

**AC-1 — CREATE TABLE on First Provision:**
**Given** a `ProvisioningJob` for a `{ designerId, version }` that has not been provisioned before
**When** the `ProvisioningBackgroundService` consumer dequeues the job
**Then** a PostgreSQL `CREATE TABLE` is issued via Dapper in an explicit transaction, with:
- Table name = `designerId` (validated `SafeIdentifier`)
- System columns: `id UUID PRIMARY KEY DEFAULT gen_random_uuid()`, `created_at TIMESTAMPTZ DEFAULT now()`, `created_by UUID REFERENCES users(id)`, `updated_at TIMESTAMPTZ`, `updated_by UUID REFERENCES users(id)`, `is_deleted BOOLEAN DEFAULT false`, `cascade_event_id UUID NULL`
- Per-fieldKey columns from the Designer's `RootElement`, mapped per Decision 1.2 (see Dev Notes)
- All dynamic (user-defined) columns are nullable — `NO NOT NULL` constraints

**AC-2 — All Dynamic Columns Are Nullable:**
**Given** any generated column from a Designer's field element
**When** the DDL is composed
**Then** the column has no `NOT NULL` constraint (per FR-24 AC-3)

**AC-3 — Transaction Rollback on Failure:**
**Given** the transaction wrapping the DDL
**When** any statement fails
**Then** the entire transaction rolls back and `provisioningStatus` flips to `"Error"` with `provisioningError` populated; no partial schema is left in PostgreSQL (per NFR-11)

**AC-4 — Idempotent If Table Already Exists:**
**Given** the target table already exists (e.g., a recovered re-run after restart)
**When** the provisioner runs
**Then** it falls through to an ALTER TABLE path — checks existing columns and adds any missing ones; the operation is a no-op if the schema already matches

**AC-5 — Schema Audit Log Appended on Success:**
**Given** a successful CREATE TABLE
**When** the transaction commits
**Then** a row is appended to `schema_audit_log` (via EF Core, after the Dapper DDL commits) with `actorId`, `createdAt`, `designerId`, `fromVersion: null`, `toVersion: version`, `ddlOperation: "CREATE"`, `columnsAdded: [...]`, `correlationId` (generated ULID per job)
**And** the schema registry entry for `(designerId, version)` is populated in-memory for future CRUD use

## Tasks / Subtasks

- [x] **Task 1 — Add Dapper NuGet package** (AC: 1, 3)
  - [x] In `Directory.Packages.props`, add: `<PackageVersion Include="Dapper" Version="2.1.66" />` (add after the existing Npgsql line)
  - [x] In `src/FormForge.Api/FormForge.Api.csproj`, add: `<PackageReference Include="Dapper" />` (in the same `<ItemGroup>` as other references)
  - [x] The Npgsql driver is already present via `Npgsql.EntityFrameworkCore.PostgreSQL` — no additional Npgsql package needed
  - [x] Also add Dapper to the test project `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` so tests can query dynamic tables directly

- [x] **Task 2 — Create `DbConnectionFactory`** (AC: 1, 3)
  - [ ] Create `src/FormForge.Api/Infrastructure/Persistence/DbConnectionFactory.cs`:
    ```csharp
    using Npgsql;
    namespace FormForge.Api.Infrastructure.Persistence;

    // Wraps raw NpgsqlConnection for Dapper DDL execution (Decision 1.6).
    // DDL paths use CommandTimeout = 60; the EF-managed static schema uses the
    // EF DbContext directly (never through this factory).
    internal sealed class DbConnectionFactory(IConfiguration configuration)
    {
        private string ConnectionString =>
            configuration.GetConnectionString("formforge")
            ?? throw new InvalidOperationException("Connection string 'formforge' not configured.");

        public async Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
        {
            var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            connection.CommandTimeout = 60;   // DDL can be slow on large tables
            return connection;
        }
    }
    ```
  - [ ] **CRITICAL**: `NpgsqlConnection.CommandTimeout` is a property on the connection object; the actual timeout applies per-command but setting it on the connection sets the default for all commands opened from it. Verify with Dapper 2.x docs that command timeout flows through via `commandTimeout` parameter or connection-level default.
  - [ ] Register as singleton in `Program.cs` (step in Task 9)

- [x] **Task 3 — Create `SchemaAuditLogEntry` entity** (AC: 5)
  - [ ] Create `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs`:
    ```csharp
    namespace FormForge.Api.Domain.Entities;

    internal sealed class SchemaAuditLogEntry
    {
        public Guid Id { get; set; }
        public string DesignerId { get; set; } = string.Empty;   // SafeIdentifier value
        public int? FromVersion { get; set; }                      // null for CREATE
        public int ToVersion { get; set; }
        public string DdlOperation { get; set; } = string.Empty;  // "CREATE" | "ALTER" | "DROP"
        public string[]? ColumnsAdded { get; set; }               // column names added by this op
        public string? CorrelationId { get; set; }                 // ULID generated per job
        public Guid? ActorId { get; set; }                         // user who triggered the bind
        public DateTimeOffset CreatedAt { get; set; }
    }
    ```

- [x] **Task 4 — Update `FormForgeDbContext` with SchemaAuditLog** (AC: 5)
  - [ ] Add `public DbSet<SchemaAuditLogEntry> SchemaAuditLog => Set<SchemaAuditLogEntry>();` to the DbSet section (after `MenuRoleAssignments`)
  - [ ] Add entity config in `OnModelCreating`:
    ```csharp
    modelBuilder.Entity<SchemaAuditLogEntry>(e =>
    {
        e.ToTable("schema_audit_log");
        e.HasKey(a => a.Id);
        e.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        e.Property(a => a.DesignerId).HasColumnName("designer_id").IsRequired().HasMaxLength(63);
        e.Property(a => a.FromVersion).HasColumnName("from_version");
        e.Property(a => a.ToVersion).HasColumnName("to_version").IsRequired();
        e.Property(a => a.DdlOperation).HasColumnName("ddl_operation").IsRequired().HasMaxLength(10);
        e.Property(a => a.ColumnsAdded).HasColumnName("columns_added")
         .HasColumnType("text[]");
        e.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasMaxLength(26);
        e.Property(a => a.ActorId).HasColumnName("actor_id");
        e.Property(a => a.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        e.HasIndex(a => new { a.DesignerId, a.CreatedAt })
         .HasDatabaseName("idx_schema_audit_log_designer_id_created_at");
        e.HasIndex(a => a.CorrelationId)
         .HasDatabaseName("idx_schema_audit_log_correlation_id");
    });
    ```
  - [ ] **Array column**: EF Core + Npgsql handles `string[]` ↔ `text[]` transparently. No extra config needed beyond `HasColumnType("text[]")`.

- [x] **Task 5 — Add EF migration: `CreateSchemaAuditLog`** (AC: 5)
  - [ ] Run: `dotnet ef migrations add CreateSchemaAuditLog --project src/FormForge.Api --startup-project src/FormForge.Api`
  - [ ] Verify the generated migration creates:
    - Table `schema_audit_log` with all 9 columns
    - Index `idx_schema_audit_log_designer_id_created_at` on `(designer_id, created_at DESC)`
    - Index `idx_schema_audit_log_correlation_id` on `correlation_id`
  - [ ] EF migrations generate `created_at DESC` indexes using `IsDescending()`. Verify EF Core 10 / Npgsql provider emits this correctly, otherwise add `migrationBuilder.Sql(...)` to fix the index direction manually.
  - [ ] Previous migration: `20260525051450_AddMenuBindingColumns.cs` — new migration must come after this one.
  - [ ] **DO NOT** run `dotnet ef database update` manually; the app runs `db.Database.MigrateAsync()` on startup.

- [x] **Task 6 — Create SchemaRegistry infrastructure** (AC: 5)

  **6a — `ColumnDefinition.cs`** (`src/FormForge.Api/Features/SchemaRegistry/ColumnDefinition.cs`):
  ```csharp
  namespace FormForge.Api.Features.SchemaRegistry;

  internal sealed record ColumnDefinition(
      string ColumnName,      // validated fieldKey — becomes the PG column name
      string PgType,          // TEXT | NUMERIC | BOOLEAN | TIMESTAMPTZ | JSONB
      string ComponentType,   // TextInput | NumberInput | Image | ...
      bool IsImage);          // drives presigned URL serialization in Epic 6
  ```

  **6b — `SchemaRegistryEntry.cs`** (`src/FormForge.Api/Features/SchemaRegistry/SchemaRegistryEntry.cs`):
  ```csharp
  namespace FormForge.Api.Features.SchemaRegistry;

  internal sealed record SchemaRegistryEntry(
      string DesignerId,
      int Version,
      IReadOnlyList<ColumnDefinition> Columns,
      IReadOnlyList<string> ChildRepeaterDesignerIds,   // for Story 5.5 cascade walk
      DateTimeOffset CachedAt);
  ```

  **6c — `ComponentTypeMapper.cs`** (`src/FormForge.Api/Features/SchemaRegistry/ComponentTypeMapper.cs`):
  ```csharp
  namespace FormForge.Api.Features.SchemaRegistry;

  // Decision 1.2 — complete 14-component PG type mapping.
  // Returns null when the component type produces no column (structural / UI-only).
  internal static class ComponentTypeMapper
  {
      // Structural and UI-only: produce no column.
      private static readonly HashSet<string> NoColumnTypes = new(StringComparer.Ordinal)
      {
          "Stack", "Row", "Tabs",
          "Label", "Button",
          "Repeater", "RepeaterField",
      };

      public static string? MapToPgType(string componentType) => componentType switch
      {
          "TextInput"      => "TEXT",
          "TextArea"       => "TEXT",
          "Dropdown"       => "TEXT",      // NOT a PG enum — enums are migration-hostile
          "ColorPicker"    => "TEXT",
          "Image"          => "TEXT",      // MinIO object key (string)
          "NumberInput"    => "NUMERIC",   // avoids float drift (not FLOAT8)
          "Checkbox"       => "BOOLEAN",
          "DateTimePicker" => "TIMESTAMPTZ",
          _ when NoColumnTypes.Contains(componentType) => null,
          _                => "JSONB",     // forward-compatibility fallback for unknown types
      };

      public static bool IsImageType(string componentType) => componentType == "Image";
  }
  ```

  **6d — `RootElementParser.cs`** (`src/FormForge.Api/Features/SchemaRegistry/RootElementParser.cs`):
  ```csharp
  using System.Text.Json;
  namespace FormForge.Api.Features.SchemaRegistry;

  // Walks the Designer's RootElement JSON tree and extracts the column definitions.
  // DFS traversal; structural containers (Stack, Row, Tabs) are entered but not
  // themselves emitted. Repeater/RepeaterField are skipped (child table, Story 5.5).
  // Duplicate fieldKeys: first occurrence wins (warning logged by caller).
  internal static class RootElementParser
  {
      public static IReadOnlyList<ColumnDefinition> Parse(string? rootElementJson)
      {
          if (string.IsNullOrWhiteSpace(rootElementJson))
              return [];

          using var doc = JsonDocument.Parse(rootElementJson);
          var root = doc.RootElement;
          var columns = new List<ColumnDefinition>();
          var seenKeys = new HashSet<string>(StringComparer.Ordinal);
          var repeaterIds = new List<string>();

          WalkElement(root, columns, seenKeys, repeaterIds);
          return columns.AsReadOnly();
      }

      // Also returns child Repeater designerIds for the SchemaRegistryEntry.
      public static (IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> ChildRepeaterIds)
          ParseFull(string? rootElementJson)
      {
          if (string.IsNullOrWhiteSpace(rootElementJson))
              return ([], []);

          using var doc = JsonDocument.Parse(rootElementJson);
          var root = doc.RootElement;
          var columns = new List<ColumnDefinition>();
          var seenKeys = new HashSet<string>(StringComparer.Ordinal);
          var repeaterIds = new List<string>();

          WalkElement(root, columns, seenKeys, repeaterIds);
          return (columns.AsReadOnly(), repeaterIds.AsReadOnly());
      }

      private static void WalkElement(
          JsonElement element,
          List<ColumnDefinition> columns,
          HashSet<string> seenKeys,
          List<string> repeaterIds)
      {
          if (!element.TryGetProperty("type", out var typeProp)) return;
          var type = typeProp.GetString() ?? string.Empty;

          // Collect child Repeater designerIds for Story 5.5 cascade provisioning.
          if (type == "Repeater")
          {
              if (element.TryGetProperty("properties", out var props) &&
                  props.TryGetProperty("rowDesignerId", out var rowId))
              {
                  var id = rowId.GetString();
                  if (!string.IsNullOrEmpty(id)) repeaterIds.Add(id);
              }
              // Repeater itself produces no column; do NOT recurse into its children
              // (they belong to the child table, provisioned by Story 5.5).
              return;
          }

          // Recurse into structural containers.
          if (element.TryGetProperty("children", out var children) &&
              children.ValueKind == JsonValueKind.Array)
          {
              foreach (var child in children.EnumerateArray())
                  WalkElement(child, columns, seenKeys, repeaterIds);
          }

          // Extract the column for this element (if it maps to a PG type).
          var pgType = ComponentTypeMapper.MapToPgType(type);
          if (pgType is null) return;   // structural or UI-only

          if (!element.TryGetProperty("properties", out var properties)) return;
          if (!properties.TryGetProperty("fieldKey", out var fieldKeyProp)) return;
          var fieldKey = fieldKeyProp.GetString();
          if (string.IsNullOrWhiteSpace(fieldKey)) return;

          // Validate fieldKey as a safe identifier (defensive — validator runs at save time).
          if (!SafeIdentifier.TryCreate(fieldKey, out _, out _)) return;

          // First-occurrence wins on duplicate fieldKey.
          if (!seenKeys.Add(fieldKey)) return;

          columns.Add(new ColumnDefinition(
              ColumnName: fieldKey,
              PgType: pgType,
              ComponentType: type,
              IsImage: ComponentTypeMapper.IsImageType(type)));
      }
  }
  ```
  **NOTE**: `RootElementParser` is in the `SchemaRegistry` namespace but references `SafeIdentifier` from `FormForge.Api.Features.Designer`. Add a `using FormForge.Api.Features.Designer;` at the top.

  **6e — `ISchemaRegistry.cs`** (`src/FormForge.Api/Features/SchemaRegistry/ISchemaRegistry.cs`):
  ```csharp
  namespace FormForge.Api.Features.SchemaRegistry;

  internal interface ISchemaRegistry
  {
      void Populate(SchemaRegistryEntry entry);
      SchemaRegistryEntry? TryGet(string designerId, int version);
  }
  ```

  **6f — `SchemaRegistry.cs`** (`src/FormForge.Api/Features/SchemaRegistry/SchemaRegistry.cs`):
  ```csharp
  using Microsoft.Extensions.Caching.Memory;
  namespace FormForge.Api.Features.SchemaRegistry;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "DI registered.")]
  internal sealed class SchemaRegistry(IMemoryCache cache) : ISchemaRegistry
  {
      private static string CacheKey(string designerId, int version) =>
          $"schema:{designerId}:{version}";

      private static readonly MemoryCacheEntryOptions EntryOptions = new()
      {
          SlidingExpiration = TimeSpan.FromHours(1),
          // Note: IMemoryCache has no built-in LRU capacity cap in .NET.
          // The 1-hour sliding TTL bounds memory growth for v1; a future story
          // can add a SizeLimit + Size=1 policy for true LRU eviction.
      };

      public void Populate(SchemaRegistryEntry entry)
      {
          ArgumentNullException.ThrowIfNull(entry);
          cache.Set(CacheKey(entry.DesignerId, entry.Version), entry, EntryOptions);
      }

      public SchemaRegistryEntry? TryGet(string designerId, int version) =>
          cache.TryGetValue(CacheKey(designerId, version), out SchemaRegistryEntry? entry)
              ? entry
              : null;
  }
  ```

- [x] **Task 7 — Create `DdlEmitter.cs`** (AC: 1, 2, 3, 4, 5)
  - [ ] Create `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`:
    ```csharp
    using System.Text;
    using Dapper;
    using FormForge.Api.Domain.Entities;
    using FormForge.Api.Features.Designer;
    using FormForge.Api.Features.SchemaRegistry;
    using FormForge.Api.Infrastructure.Persistence;
    using Microsoft.EntityFrameworkCore;
    using Npgsql;
    using Ulid;

    namespace FormForge.Api.Features.Provisioning;

    // Emits CREATE TABLE (new binding) or ALTER TABLE ... ADD COLUMN (idempotent re-run).
    // Uses Dapper + raw NpgsqlConnection for DDL (Decision 1.6: EF owns static schema;
    // Dapper owns dynamic-schema DDL). The FormForgeDbContext is used ONLY for:
    //   1. Reading ComponentSchemaVersion.RootElement (the schema source of truth)
    //   2. Appending a SchemaAuditLogEntry (EF entity — NOT saved here; caller SaveChanges)
    // Both operations go through the same scoped DbContext as the BackgroundService.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "DI registered.")]
    internal sealed class DdlEmitter(
        FormForgeDbContext db,
        DbConnectionFactory connectionFactory,
        ISchemaRegistry schemaRegistry,
        ILogger<DdlEmitter> logger)
    {
        public async Task EmitAsync(ProvisioningJob job, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(job);

            // 1. Load the designer version to get the RootElement.
            var version = await db.ComponentSchemaVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    v => v.DesignerId == job.DesignerId && v.Version == job.Version,
                    ct)
                .ConfigureAwait(false);

            if (version is null)
                throw new InvalidOperationException(
                    $"Version {job.Version} of designer '{job.DesignerId}' not found in component_schema_versions.");

            // 2. Re-validate the identifier as a defence-in-depth measure.
            // DesignerId was validated at bind-time but the job record carries a plain string.
            if (!SafeIdentifier.TryCreate(job.DesignerId, out var tableName, out _))
                throw new InvalidOperationException(
                    $"DesignerId '{job.DesignerId}' is not a safe PostgreSQL identifier.");

            // 3. Parse the RootElement to extract column definitions.
            var (columns, childRepeaterIds) = RootElementParser.ParseFull(version.RootElement);

            // 4. Run DDL in an explicit Dapper transaction.
            string correlationId = Ulid.NewUlid().ToString();
            string[] columnsAdded;

            await using var connection = await connectionFactory
                .CreateOpenConnectionAsync(ct)
                .ConfigureAwait(false);

            var tableExists = await TableExistsAsync(connection, tableName!.Value, ct)
                .ConfigureAwait(false);

            if (!tableExists)
            {
                columnsAdded = await CreateTableAsync(connection, tableName!, columns, ct)
                    .ConfigureAwait(false);
                LogCreatedTable(logger, tableName.Value, columnsAdded.Length);
            }
            else
            {
                // Idempotent fallthrough (AC-4): table already exists — add any missing columns.
                // Full diff (orphan detection, ALTER TABLE from version diff) is Story 5.4.
                columnsAdded = await AddMissingColumnsAsync(connection, tableName!, columns, ct)
                    .ConfigureAwait(false);
                LogAlteredTable(logger, tableName.Value, columnsAdded.Length);
            }

            // 5. Populate schema registry for future CRUD use (AR-7).
            schemaRegistry.Populate(new SchemaRegistryEntry(
                job.DesignerId,
                job.Version,
                columns,
                childRepeaterIds,
                DateTimeOffset.UtcNow));

            // 6. Append audit log row (NOT saved yet — BackgroundService.ProcessJobAsync
            //    calls db.SaveChangesAsync in its finally block, which commits both the
            //    menu status update and this audit entry together).
            db.SchemaAuditLog.Add(new SchemaAuditLogEntry
            {
                DesignerId = job.DesignerId,
                FromVersion = null,   // null = CREATE (no prior version)
                ToVersion = job.Version,
                DdlOperation = tableExists ? "ALTER" : "CREATE",
                ColumnsAdded = columnsAdded,
                CorrelationId = correlationId,
                ActorId = job.ActorId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        private static async Task<bool> TableExistsAsync(
            NpgsqlConnection connection, string tableName, CancellationToken ct)
        {
            const string sql = """
                SELECT COUNT(1) > 0
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                """;
            return await connection
                .ExecuteScalarAsync<bool>(sql, new { tableName })
                .ConfigureAwait(false);
        }

        private static async Task<string[]> CreateTableAsync(
            NpgsqlConnection connection,
            SafeIdentifier tableName,
            IReadOnlyList<ColumnDefinition> columns,
            CancellationToken ct)
        {
            var userColumnsSql = BuildUserColumnsSql(columns);
            // System columns are hardcoded strings — no user input interpolated.
            // User column names come only from SafeIdentifier-validated fieldKeys.
            var sql = $"""
                CREATE TABLE {tableName.Value} (
                    id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
                    created_at      TIMESTAMPTZ  DEFAULT now(),
                    created_by      UUID         REFERENCES users(id),
                    updated_at      TIMESTAMPTZ,
                    updated_by      UUID         REFERENCES users(id),
                    is_deleted      BOOLEAN      DEFAULT false,
                    cascade_event_id UUID        NULL{(userColumnsSql.Length > 0 ? "," : "")}
                {userColumnsSql}
                )
                """;

            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.ExecuteAsync(sql, transaction: tx).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }

            return [.. columns.Select(c => c.ColumnName)];
        }

        private static async Task<string[]> AddMissingColumnsAsync(
            NpgsqlConnection connection,
            SafeIdentifier tableName,
            IReadOnlyList<ColumnDefinition> columns,
            CancellationToken ct)
        {
            // Fetch existing columns from information_schema.
            const string existingSql = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                """;
            var existing = (await connection
                .QueryAsync<string>(existingSql, new { tableName = tableName.Value })
                .ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            var toAdd = columns.Where(c => !existing.Contains(c.ColumnName)).ToList();
            if (toAdd.Count == 0) return [];

            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var col in toAdd)
                {
                    // col.ColumnName is a validated SafeIdentifier value.
                    var alterSql = $"ALTER TABLE {tableName.Value} ADD COLUMN {col.ColumnName} {col.PgType} NULL";
                    await connection.ExecuteAsync(alterSql, transaction: tx).ConfigureAwait(false);
                }
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }

            return [.. toAdd.Select(c => c.ColumnName)];
        }

        private static string BuildUserColumnsSql(IReadOnlyList<ColumnDefinition> columns)
        {
            if (columns.Count == 0) return string.Empty;
            // Each column name is a SafeIdentifier-validated fieldKey — safe to interpolate.
            var sb = new StringBuilder();
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                sb.Append($"    {col.ColumnName,-30} {col.PgType,-15} NULL");
                if (i < columns.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            return sb.ToString();
        }

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Created table '{TableName}' with {ColumnCount} user-defined column(s)")]
        private static partial void LogCreatedTable(ILogger logger, string tableName, int columnCount);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Altered table '{TableName}' — added {ColumnCount} missing column(s)")]
        private static partial void LogAlteredTable(ILogger logger, string tableName, int columnCount);
    }
    ```
  - [ ] **SQL injection prevention**: user-authored strings (fieldKey, designerId) are ONLY used as `SafeIdentifier.Value` before interpolation into SQL. The PG type (`col.PgType`) comes from the hardcoded `ComponentTypeMapper` map — never from user input. No raw string concatenation of user input is ever interpolated into SQL.
  - [ ] Add `internal sealed partial class DdlEmitter` declaration — the `partial` keyword is needed for `[LoggerMessage]` source-generated partials. Ensure the file's class declaration uses `partial`.
  - [ ] **NpgsqlConnection lifetime**: use `await using` (not `using`) for `NpgsqlConnection` and `NpgsqlTransaction` — they implement `IAsyncDisposable`.

- [x] **Task 8 — Update `ProvisioningBackgroundService.cs`** (AC: 1, 2, 3)
  - [ ] Replace the stub try block in `ProcessJobAsync` with the real `DdlEmitter` call:
    ```csharp
    private async Task ProcessJobAsync(ProvisioningJob job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        var menu = await db.Menus
            .FirstOrDefaultAsync(m => m.Id == job.MenuId, CancellationToken.None)
            .ConfigureAwait(false);
        if (menu is null)
        {
            LogMenuMissing(logger, job.MenuId);
            return;
        }

        if (menu.ProvisioningStatus != "Pending")
        {
            LogNotPending(logger, job.MenuId, menu.ProvisioningStatus);
            return;
        }

        try
        {
            var emitter = scope.ServiceProvider.GetRequiredService<DdlEmitter>();
            // CancellationToken.None: once a job is dequeued we run it to completion.
            // Host shutdown is honoured at the ReadAllAsync loop boundary.
            await emitter.EmitAsync(job, CancellationToken.None).ConfigureAwait(false);

            menu.ProvisioningStatus = "Success";
            menu.ProvisioningError = null;
            LogProvisioningSucceeded(logger, job.MenuId, job.DesignerId, job.Version);
        }
        catch (OperationCanceledException)
        {
            // Re-throw so the host-shutdown path does not record a spurious "Error"
            // status. The Story 5.8 recovery scanner will re-enqueue Pending rows
            // on next startup. (Deferred fix from Story 5.2 code review.)
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProvisioningFailed(logger, ex, job.MenuId, job.DesignerId, job.Version);
            menu.ProvisioningStatus = "Error";
            menu.ProvisioningError = ex.Message;
        }
        finally
        {
            menu.UpdatedAt = DateTimeOffset.UtcNow;
            // Commits: menu status update + any pending SchemaAuditLogEntry added by DdlEmitter.
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
    ```
  - [ ] Update the `[LoggerMessage]` partials — remove the old stub log line, add `LogProvisioningSucceeded`:
    ```csharp
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Provisioned MenuId {MenuId} DesignerId {DesignerId} v{Version} — Success")]
    private static partial void LogProvisioningSucceeded(
        ILogger logger, Guid menuId, string designerId, int version);
    ```
  - [ ] Remove `LogProvisioningStub` partial (no longer needed) — it was the Story 5.2 stub log line.
  - [ ] **Constructor injection update**: `DdlEmitter` is resolved from the per-job scope (not injected directly into the BackgroundService constructor), because `DdlEmitter` is scoped. The BackgroundService constructor still injects only `ChannelReader<ProvisioningJob>`, `IServiceScopeFactory`, and `ILogger<ProvisioningBackgroundService>`.

- [x] **Task 9 — Register new services in `Program.cs`** (AC: 1, 5)
  - [ ] Add registrations BEFORE the existing `IMenuService` registration (within the provisioning block at line 131+):
    ```csharp
    // Story 5.3 — DbConnectionFactory (singleton: wraps NpgsqlConnection for Dapper DDL)
    builder.Services.AddSingleton<DbConnectionFactory>();

    // Story 5.3 — SchemaRegistry (singleton: in-memory cache keyed by (designerId, version))
    builder.Services.AddSingleton<ISchemaRegistry, SchemaRegistry>();

    // Story 5.3 — DdlEmitter (scoped: injects FormForgeDbContext which is scoped)
    builder.Services.AddScoped<DdlEmitter>();
    ```
  - [ ] Add necessary `using` directives:
    - `using FormForge.Api.Features.SchemaRegistry;`
    - `DbConnectionFactory` is in `FormForge.Api.Infrastructure.Persistence` — already imported or add the using
  - [ ] `IMemoryCache` is registered by `builder.Services.AddMemoryCache()` — verify this is already called in Program.cs (it is, from earlier stories' IMenuCache / PermissionService setup). If not, add `builder.Services.AddMemoryCache()`.

- [x] **Task 10 — Update `ProvisioningIntegrationTests.cs`** (AC: 1–5)

  **10a — Update InitializeAsync to drop dynamic tables:**
  ```csharp
  // Drop any dynamically-provisioned tables from previous test runs.
  // These tables live outside the static EF schema and are not reached by TRUNCATE CASCADE.
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
  ```
  Add this AFTER the TRUNCATE statement but BEFORE `ReseedSystemRolesAsync`.

  **10b — Add helper for creating a designer with field components:**
  ```csharp
  private async Task CreateAndPublishDesignerWithFieldsAsync(
      string token,
      string designerId,
      object rootElement)
  {
      await CreateDesignerViaApiAsync(token, designerId);
      // Override PostVersionAsync to accept a custom rootElement
      using var request = new HttpRequestMessage(
          HttpMethod.Post, $"/api/designers/{designerId}/versions")
      {
          Content = JsonContent.Create(new { rootElement }),
      };
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
      using var response = await _client!.SendAsync(request);
      response.EnsureSuccessStatusCode();
      await PutVersionStatusAsync(token, designerId, 1, "Published");
  }
  ```

  Also add a helper to check if a PostgreSQL table exists (via direct SQL):
  ```csharp
  private async Task<bool> TableExistsInPostgresAsync(string tableName)
  {
      // Use the postgres fixture connection string to bypass the app layer.
      await using var connection = new Npgsql.NpgsqlConnection(_postgres.ConnectionString);
      await connection.OpenAsync();
      using var cmd = connection.CreateCommand();
      cmd.CommandText = """
          SELECT COUNT(1) > 0
          FROM information_schema.tables
          WHERE table_schema = 'public' AND table_name = @tableName
          """;
      cmd.Parameters.AddWithValue("tableName", tableName);
      return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private async Task<IReadOnlyList<(string Name, string DataType)>> GetTableColumnsAsync(string tableName)
  {
      await using var connection = new Npgsql.NpgsqlConnection(_postgres.ConnectionString);
      await connection.OpenAsync();
      using var cmd = connection.CreateCommand();
      cmd.CommandText = """
          SELECT column_name, data_type
          FROM information_schema.columns
          WHERE table_schema = 'public' AND table_name = @tableName
          ORDER BY ordinal_position
          """;
      cmd.Parameters.AddWithValue("tableName", tableName);
      await using var reader = await cmd.ExecuteReaderAsync();
      var result = new List<(string, string)>();
      while (await reader.ReadAsync())
          result.Add((reader.GetString(0), reader.GetString(1)));
      return result;
  }
  ```

  **10c — New test cases (+7):**

  ```csharp
  [Fact]
  public async Task ProvisionNewTable_EmptyDesigner_CreatesTableWithSystemColumnsOnly()
  {
      // AC-1: empty Stack root → only system columns in the created table.
      var token = await LoginAsync("admin@example.com", "Password1!");
      var menuId = await CreateMenuViaApiAsync(token, "EmptyDesignerMenu", 0);
      await CreateAndPublishDesignerAsync(token, "empty_designer");   // uses empty Stack

      using (var bind = await PutBindingAsync(token, menuId, "empty_designer", 1))
          Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

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
      // AC-1, AC-2: TextInput → TEXT (nullable), NumberInput → NUMERIC (nullable).
      var token = await LoginAsync("admin@example.com", "Password1!");
      var menuId = await CreateMenuViaApiAsync(token, "TextNumericMenu", 0);

      var rootElement = new
      {
          id = "root", type = "Stack", properties = new { },
          children = new object[]
          {
              new { id = "f1", type = "TextInput",
                    properties = new { fieldKey = "full_name" }, children = Array.Empty<object>() },
              new { id = "f2", type = "NumberInput",
                    properties = new { fieldKey = "age" }, children = Array.Empty<object>() },
              new { id = "f3", type = "TextArea",
                    properties = new { fieldKey = "notes" }, children = Array.Empty<object>() },
          }
      };
      await CreateAndPublishDesignerWithFieldsAsync(token, "text_numeric_fields", rootElement);

      using (var bind = await PutBindingAsync(token, menuId, "text_numeric_fields", 1))
          Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

      var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
      Assert.Equal("Success", status);

      var cols = await GetTableColumnsAsync("text_numeric_fields");
      var colMap = cols.ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
      Assert.Equal("text", colMap["full_name"]);
      Assert.Equal("numeric", colMap["age"]);
      Assert.Equal("text", colMap["notes"]);
      // Verify AC-2: all user columns are nullable (no NOT NULL → IS_NULLABLE = 'YES').
      // This is checked implicitly — PostgreSQL information_schema.columns.is_nullable.
      // Simple proxy: can INSERT a row with NULL values for all user columns.
      await using var conn = new Npgsql.NpgsqlConnection(_postgres.ConnectionString);
      await conn.OpenAsync();
      using var cmd = conn.CreateCommand();
      cmd.CommandText =
          "INSERT INTO text_numeric_fields (full_name, age, notes) VALUES (NULL, NULL, NULL)";
      await cmd.ExecuteNonQueryAsync();   // must not throw
  }

  [Fact]
  public async Task ProvisionNewTable_WithBooleanAndDateTimeFields_CreatesCorrectPgColumns()
  {
      // AC-1: Checkbox → BOOLEAN, DateTimePicker → TIMESTAMPTZ, Dropdown → TEXT, ColorPicker → TEXT, Image → TEXT.
      var token = await LoginAsync("admin@example.com", "Password1!");
      var menuId = await CreateMenuViaApiAsync(token, "BoolDateMenu", 0);

      var rootElement = new
      {
          id = "root", type = "Stack", properties = new { },
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
              new { id = "f5", type = "Image",
                    properties = new { fieldKey = "photo" }, children = Array.Empty<object>() },
          }
      };
      await CreateAndPublishDesignerWithFieldsAsync(token, "bool_date_fields", rootElement);

      using (var bind = await PutBindingAsync(token, menuId, "bool_date_fields", 1))
          Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

      var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
      Assert.Equal("Success", status);

      var cols = await GetTableColumnsAsync("bool_date_fields");
      var colMap = cols.ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
      Assert.Equal("boolean", colMap["is_active"]);
      Assert.Equal("timestamp with time zone", colMap["occurred_at"]);
      Assert.Equal("text", colMap["category"]);
      Assert.Equal("text", colMap["bg_color"]);
      Assert.Equal("text", colMap["photo"]);
  }

  [Fact]
  public async Task ProvisionNewTable_StructuralAndUiOnlyComponents_ProduceNoColumns()
  {
      // AC-1: Stack, Row, Label, Button → no user columns; only 7 system columns.
      var token = await LoginAsync("admin@example.com", "Password1!");
      var menuId = await CreateMenuViaApiAsync(token, "StructuralMenu", 0);

      var rootElement = new
      {
          id = "root", type = "Stack", properties = new { },
          children = new object[]
          {
              new { id = "r1", type = "Row", properties = new { }, children = new object[]
              {
                  new { id = "l1", type = "Label",
                        properties = new { label = "Title" }, children = Array.Empty<object>() },
                  new { id = "b1", type = "Button",
                        properties = new { label = "Submit" }, children = Array.Empty<object>() },
              }},
          }
      };
      await CreateAndPublishDesignerWithFieldsAsync(token, "structural_only", rootElement);

      using (var bind = await PutBindingAsync(token, menuId, "structural_only", 1))
          Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

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
          Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

      var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
      Assert.Equal("Success", status);

      // Query schema_audit_log directly.
      await using var conn = new Npgsql.NpgsqlConnection(_postgres.ConnectionString);
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
      Assert.True(reader.IsDBNull(1), "from_version should be NULL for CREATE");
      Assert.Equal(1, reader.GetInt32(2));
      // correlation_id is a ULID (26 chars)
      Assert.Equal(26, reader.GetString(4).Length);
  }

  [Fact]
  public async Task ProvisionNewTable_IdempotentWhenTableExists_NoColumnDuplicated()
  {
      // AC-4: binding the same version twice → the second run succeeds (no error),
      // does not duplicate columns, and adds an ALTER audit log row.
      var token = await LoginAsync("admin@example.com", "Password1!");
      var menuId = await CreateMenuViaApiAsync(token, "IdempotentMenu", 0);
      await CreateAndPublishDesignerAsync(token, "idempotent_designer");

      // First bind → CREATE TABLE.
      using (var bind = await PutBindingAsync(token, menuId, "idempotent_designer", 1))
          Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
      var status = await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10));
      Assert.Equal("Success", status);

      // Second bind (retry path) → falls through to AddMissingColumns, no-op.
      using (var retry = await PostRetryAsync(token, menuId))
          Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
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
          Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);

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
  ```

  - [ ] **Test class** needs `using FormForge.Api.Features.SchemaRegistry;` for the `ISchemaRegistry` test (#7 above)
  - [ ] **Test baseline: 331** (end of Story 5.2 code review). Estimated additions: **+7 integration tests**. Target: **338**.
  - [ ] All 331 existing tests must still pass. The existing `BindDesigner_ValidPublishedVersion_Returns202AndProvisionsAsync` test continues to work — it now exercises the REAL DDL path (empty Stack → 7 system columns only). No changes needed to that test.

## Dev Notes

### What Already Exists — Read Before Writing Any Code

**Provisioning infrastructure (Story 5.2):**
- `src/FormForge.Api/Features/Provisioning/ProvisioningJob.cs` — positional record `(Guid MenuId, string DesignerId, int Version, Guid? ActorId)`
- `src/FormForge.Api/Features/Provisioning/IProvisioningService.cs` — `ValueTask EnqueueAsync(ProvisioningJob job, CancellationToken ct)`
- `src/FormForge.Api/Features/Provisioning/ProvisioningService.cs` — ChannelWriter wrapper
- `src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs` — **MUST BE MODIFIED** (Task 8) — replace stub with DdlEmitter call + fix OCE catch
- `src/FormForge.Api/Features/Provisioning/BindingDiffService.cs` — stub, untouched by Story 5.3

**Domain entities:**
- `src/FormForge.Api/Domain/Entities/ComponentSchemaVersion.cs` — has `DesignerId`, `Version`, `Status`, `RootElement` (JSON string as TEXT, not JSONB-parsed by EF)
- `src/FormForge.Api/Domain/Entities/ComponentSchema.cs` — PK is `DesignerId: string`

**SafeIdentifier** (`src/FormForge.Api/Features/Designer/SafeIdentifier.cs`):
- Already validates `^[a-z_][a-z0-9_]{0,62}$` + reserved keyword check
- `TryCreate(raw, out result, out errorCode, out error)` — use this in DdlEmitter
- `Value` property gives the validated string safe to interpolate into SQL

**Database:**
- `FormForgeDbContext` has `ComponentSchemaVersions` and `Menus` DbSets
- Last migration: `20260525051450_AddMenuBindingColumns`
- The `formforge` connection string is used by EF and also by `DbConnectionFactory`

**Program.cs provisioning block** (around line 131):
```csharp
var provisioningChannel = System.Threading.Channels.Channel.CreateBounded<ProvisioningJob>(256);
builder.Services.AddSingleton(provisioningChannel.Reader);
builder.Services.AddSingleton(provisioningChannel.Writer);
builder.Services.AddSingleton<IProvisioningService, ProvisioningService>();
builder.Services.AddScoped<BindingDiffService>();
builder.Services.AddHostedService<ProvisioningBackgroundService>();
builder.Services.AddScoped<IValidator<BindMenuDesignerRequest>, BindMenuDesignerRequestValidator>();
```
Add the three new registrations (DbConnectionFactory, ISchemaRegistry, DdlEmitter) immediately before or after this block.

### Decision 1.2 — Complete Component-to-PG Type Mapping

| Component Type | PG Type | Notes |
|---|---|---|
| `Stack`, `Row`, `Tabs` | — (no column) | Structural containers |
| `Label`, `Button` | — (no column) | UI-only |
| `Repeater` | — (no column) | Triggers child table (Story 5.5) |
| `RepeaterField` | — (no column) | Lives in child table |
| `TextInput` | `TEXT` | |
| `TextArea` | `TEXT` | |
| `Dropdown` | `TEXT` | NOT PG enum — enums are migration-hostile; stores option `value` |
| `ColorPicker` | `TEXT` | Hex string `#RRGGBB` |
| `Image` | `TEXT` | MinIO object key |
| `NumberInput` | `NUMERIC` | Avoids float drift (not FLOAT8) |
| `Checkbox` | `BOOLEAN` | |
| `DateTimePicker` | `TIMESTAMPTZ` | |
| Unknown / future | `JSONB` | Forward-compat fallback |

### System Columns (AC-1)

Every dynamic table gets these 7 system columns:
```sql
id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
created_at      TIMESTAMPTZ  DEFAULT now(),
created_by      UUID         REFERENCES users(id),
updated_at      TIMESTAMPTZ,
updated_by      UUID         REFERENCES users(id),
is_deleted      BOOLEAN      DEFAULT false,
cascade_event_id UUID        NULL
```
`cascade_event_id` is for cascade soft-delete in Epic 6 (Decision 1.3). It is never `NOT NULL`.

### EF/Dapper Boundary (Decision 1.6)

- **EF Core** handles all static-schema writes: `ComponentSchemaVersions` reads, `SchemaAuditLogEntry` writes, `Menu` status updates.
- **Dapper + NpgsqlConnection** handles all dynamic-schema DDL: `CREATE TABLE`, `ALTER TABLE ... ADD COLUMN`.
- The Dapper DDL transaction and the EF SaveChanges are **separate transactions**. Ordering:
  1. Dapper DDL transaction commits (table is created/altered in PG)
  2. `DdlEmitter.EmitAsync` adds `SchemaAuditLogEntry` to the DbContext (without saving)
  3. `ProvisioningBackgroundService.ProcessJobAsync` sets `menu.ProvisioningStatus = "Success"`
  4. The `finally` block calls `db.SaveChangesAsync(CancellationToken.None)` — commits both menu status update AND the audit log entry in a single EF transaction

If step 4 fails after step 1, the table exists but status stays Pending (recoverable via Story 5.8 recovery scanner). This dual-write hazard is accepted for v1.

### Dapper Package — Version Selection

Dapper 2.1.66 (latest stable as of 2026-05) is used. The Npgsql driver is already in scope via `Npgsql.EntityFrameworkCore.PostgreSQL`. No additional `Npgsql` standalone package is needed.

**IMPORTANT**: The project uses Central Package Management (`Directory.Packages.props`). You MUST add the `<PackageVersion>` entry there AND the `<PackageReference>` in the `.csproj`. Failing to add to `Directory.Packages.props` causes a build error ("... Packages configures to use central package management, but 'Dapper' doesn't have a corresponding entry").

### SQL Injection Prevention (NFR-6)

The only user-authored strings that reach DDL are:
- `designerId` (table name) — always passed through `SafeIdentifier.TryCreate()`; the `SafeIdentifier.Value` is interpolated.
- `fieldKey` (column names) — validated by `FieldKeyValidator` at save time; additionally re-validated by `RootElementParser` calling `SafeIdentifier.TryCreate(fieldKey, ...)`.

**Never** interpolate raw user strings into SQL. The pattern to follow in DdlEmitter:
```csharp
// CORRECT — tableName.Value is a SafeIdentifier-validated string
$"CREATE TABLE {tableName.Value} ..."

// WRONG — never do this:
$"CREATE TABLE {job.DesignerId} ..."
```

PG types (`col.PgType`) come from the hardcoded `ComponentTypeMapper` dictionary — they are never user input.

### NpgsqlConnection and Dapper Transaction Pattern

```csharp
await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
await using var tx = await connection.BeginTransactionAsync(ct);
try
{
    await connection.ExecuteAsync(sql, transaction: tx);
    await tx.CommitAsync(ct);
}
catch
{
    await tx.RollbackAsync(ct);
    throw;   // let BackgroundService's catch-all record Error status
}
```

Use `await using` (not `using`) for `NpgsqlConnection` and `NpgsqlTransaction` — they implement `IAsyncDisposable`. Calling `Dispose()` synchronously on a connection that has a pending async op can cause issues.

### RootElement JSON Shape

The `ComponentSchemaVersion.RootElement` is a JSON string (stored as `TEXT` in PostgreSQL, not JSONB-parsed by EF). The structure mirrors the Designer canvas:
```json
{
  "id": "root",
  "type": "Stack",
  "properties": {},
  "children": [
    {
      "id": "el-1",
      "type": "TextInput",
      "properties": { "fieldKey": "first_name", "label": "First Name" },
      "children": []
    }
  ]
}
```

`RootElementParser.ParseFull()` uses `System.Text.Json.JsonDocument.Parse()` (built-in to .NET) — no extra JSON library needed. `JsonDocument` is disposable (`using var doc = JsonDocument.Parse(...)`).

### SchemaRegistry Singleton Pattern

`SchemaRegistry` is registered as a singleton. It wraps `IMemoryCache` (also singleton, registered by `AddMemoryCache()`). The cache key format is `schema:{designerId}:{version}`. 

The registry is populated by `DdlEmitter.EmitAsync()` (called from the BackgroundService scope). Since `ISchemaRegistry` is singleton, it is accessible from any scope — including the BackgroundService's per-job scope. This is correct.

### Deferred Work from Story 5.2 Applied in This Story

**Apply now in Task 8:**
> "OperationCanceledException inside the try block will be recorded as Error status" — add `catch (OperationCanceledException) { throw; }` before the general catch-all in `ProcessJobAsync`. The stub try block in 5.2 had no async I/O so this couldn't trigger. Story 5.3's DdlEmitter runs async Npgsql/Dapper calls that DO respect cancellation.

**Still deferred (Story 5.6 / 5.8):**
- `GetBindingDiffHandler` targetVersion=0 guard (Story 5.6)
- `EnqueueAsync` failure post-commit leaves row Pending (Story 5.8 recovery scanner)

### LoggerMessage Partials — `partial class` Requirement

`DdlEmitter` uses `[LoggerMessage]` source-generated partials. The class declaration must be `partial`:
```csharp
internal sealed partial class DdlEmitter(...)
```

Same pattern used by `ProvisioningBackgroundService` — look at that file for the exact pattern.

### Test Isolation: Dynamic Table Cleanup

The dynamically-created PostgreSQL tables (`CREATE TABLE {designerId}`) persist across tests because they are outside the EF-managed static schema. The TRUNCATE statement in `InitializeAsync` does NOT drop them.

**Task 10a** adds a PL/pgSQL block to drop all public tables not in the known static list. This runs in every `InitializeAsync` so each test starts clean. The list of known static tables must include `schema_audit_log` (added by Task 5).

### File Locations

New files to create (following architecture dir tree):
- `src/FormForge.Api/Infrastructure/Persistence/DbConnectionFactory.cs` — NOT in `Features/`
- `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs` — alongside other entities
- `src/FormForge.Api/Features/SchemaRegistry/` — new folder matching the architecture spec
  - `ColumnDefinition.cs`, `SchemaRegistryEntry.cs`, `ComponentTypeMapper.cs`
  - `RootElementParser.cs`, `ISchemaRegistry.cs`, `SchemaRegistry.cs`
- `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs` — alongside existing provisioning files

**Architecture doc mismatch**: `Features/Designer/` (actual) vs `Features/Designers/` (doc). For `SchemaRegistry` and `Provisioning`, the doc says `Features/SchemaRegistry/` and `Features/Provisioning/` — these are new folders, so follow the doc exactly.

### What This Story Does NOT Implement

- **Story 5.4** — `ALTER TABLE ... ADD COLUMN` from a version diff (re-bind to a new designer version). Story 5.3 only implements the idempotent fallthrough for EXISTING tables (re-run of the same version).
- **Story 5.5** — Repeater child table provisioning. `RootElementParser` records `ChildRepeaterDesignerIds` but does not provision child tables.
- **Story 5.6** — real `pg_attribute` introspection for `BindingDiffService`. The stub remains.
- **Story 5.8** — `ProvisioningRecoveryService`. The filtered index on `provisioning_status = 'Pending'` already exists from Story 5.2.
- **CycleDetector.cs** — Repeater cycle detection (Story 5.5).
- **Dynamic CRUD** (`Features/DynamicCrud/`) — Epic 6. `ISchemaRegistry.TryGet()` is implemented now but will first be called in Story 6.1.

### Test Count Summary

| Location | Change |
|---|---|
| `ProvisioningIntegrationTests.cs` | +7 new integration tests |
| Existing 331 tests | must all still pass |
| **Baseline** | 331 (end of Story 5.2 code review) |
| **Estimated total** | 338 |

`dotnet build` must be clean (0 warnings, 0 errors). `dotnet test 338/338`. Frontend unchanged.

### Project Structure Notes

- `DbConnectionFactory` lives in `Infrastructure/Persistence/` alongside `FormForgeDbContext.cs` — not in `Features/` (it is infrastructure, not a domain feature)
- `SchemaAuditLogEntry` lives in `Domain/Entities/` — not in `Features/Provisioning/` (it is a domain entity managed by EF, per Decision 1.5)
- `SchemaRegistry` folder contains 6 files — create them all in one commit pass
- The `using FormForge.Api.Features.Designer;` import is needed in `RootElementParser.cs` to reference `SafeIdentifier`

### References

- **Decision 1.1** (SafeIdentifier): `_bmad-output/planning-artifacts/architecture.md`
- **Decision 1.2** (Component → PG type mapping): `architecture.md`
- **Decision 1.3** (soft-delete cascade column): `architecture.md`
- **Decision 1.4** (SchemaRegistry cache): `architecture.md`
- **Decision 1.5** (audit log indexes): `architecture.md`
- **Decision 1.6** (EF/Dapper boundary + recovery): `architecture.md`
- **NFR-6** (SQL injection defense): via `SafeIdentifier` — `src/FormForge.Api/Features/Designer/SafeIdentifier.cs`
- **NFR-11** (transactional DDL with rollback): explicit Dapper transaction with try/catch/rollback
- **Story 5.3 epics spec**: `_bmad-output/planning-artifacts/epics.md` (Epic 5, Story 5.3 section)
- **Previous story**: `_bmad-output/implementation-artifacts/5-2-bind-designer-version-to-menu-item.md`
- **Deferred work (OCE fix applied in Task 8)**: `_bmad-output/implementation-artifacts/deferred-work.md` line 8
- **Existing ProvisioningBackgroundService** (stub to replace): `src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs`
- **Existing SafeIdentifier**: `src/FormForge.Api/Features/Designer/SafeIdentifier.cs`
- **Existing ComponentSchemaVersion entity**: `src/FormForge.Api/Domain/Entities/ComponentSchemaVersion.cs`
- **Existing FormForgeDbContext**: `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`
- **Existing ProvisioningIntegrationTests**: `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`
- **Directory.Packages.props** (Central Package Management): `Directory.Packages.props`
- **Program.cs** (provisioning block at ~line 131): `src/FormForge.Api/Program.cs`

## Dev Agent Record

### Agent Model Used

claude-opus-4-7 (Opus 4.7, 1M context)

### Debug Log References

- Initial backend build failed CA1849 (`NpgsqlConnection.CommandTimeout` is read-only at runtime). Story spec flagged this explicitly. Resolved by exposing `DbConnectionFactory.DdlCommandTimeoutSeconds = 60` and passing it through Dapper's per-command `commandTimeout` parameter rather than setting on the connection.
- Initial backend build also failed CA2007 on `await using var` for `NpgsqlConnection` and `NpgsqlTransaction` (the implicit `DisposeAsync` is not ConfigureAwait-safe). Resolved by converting to explicit try/finally with `await disposable.DisposeAsync().ConfigureAwait(false)` in the finally block, preserving correct disposal-on-exception semantics.
- New EF migration `CreateSchemaAuditLog.cs` failed CA1062 (parameter null-check on `migrationBuilder`). Matches the same fix applied in `AddMenuBindingColumns` from Story 5.2 — added `ArgumentNullException.ThrowIfNull(migrationBuilder)` to both `Up()` and `Down()`.
- New integration test `ProvisionNewTable_WithTextAndNumericFields_CreatesCorrectPgColumns` initially failed because `colMap["full_name"]` threw `KeyNotFoundException`. Root cause was a two-bug interaction in the test helper, NOT a defect in `RootElementParser` or `DdlEmitter`:
  1. `CreateAndPublishDesignerWithFieldsAsync` originally took `object rootElement` and serialised via `JsonContent.Create(new { rootElement })`. `JsonSerializer` collapsed the `object`-typed member to `{}` because polymorphic resolution uses the declared type, not the runtime type. The wire format was sent as `{"rootElement":{}}` which deserialised into a `JsonObject` with no children → `SaveVersionAsync` stored `null`.
  2. After switching to explicit `JsonSerializer.Serialize<TRoot>(rootElement, WebJsonOptions)` + raw `StringContent`, the request body became correct, but the helper still called `PutVersionStatusAsync(token, designerId, 1, "Published")` — which publishes v1 (the null-rootElement version created by `CreateDesignerViaApiAsync`) rather than v2 (the version carrying the test's actual rootElement). Test then bound to v1, found null root, no columns. Final fix: helper POSTs v2, publishes v2, returns the published version number; tests bind to the returned version.
- Added `RootElementParserTests.cs` (9 unit tests) during the diagnosis loop above so the parser can be exercised independently of the bind/provision pipeline in future regressions. One test draft used `fieldKey: "when"` which is a PG reserved keyword — fixed to `due_at` after the parser correctly dropped it.

### Completion Notes List

- All 5 ACs implemented and verified by 7 new integration tests (+9 unit tests for the parser).
- AC-1 (CREATE TABLE on first provision): table is created with 7 hardcoded system columns plus all user fieldKey columns mapped per Decision 1.2. SQL is interpolated using `SafeIdentifier.Value` only (defence-in-depth re-validation in both `DdlEmitter.EmitAsync` and `RootElementParser.WalkElement`).
- AC-2 (all dynamic columns nullable): every column in `BuildUserColumnsSql` ends with `NULL`. Verified by `INSERT NULL, NULL, NULL` in `ProvisionNewTable_WithTextAndNumericFields_CreatesCorrectPgColumns`.
- AC-3 (transaction rollback on failure): both `CreateTableAsync` and `AddMissingColumnsAsync` wrap DDL in `BeginTransactionAsync` and rollback on exception. The catch-all in `ProvisioningBackgroundService.ProcessJobAsync` records `Error` + the message on the menu row.
- AC-4 (idempotent if table already exists): `TableExistsAsync` short-circuits to `AddMissingColumnsAsync` which queries `information_schema.columns`, computes the diff, and only emits `ALTER TABLE ADD COLUMN` for the missing entries. Regression-guarded by `ProvisionNewTable_IdempotentWhenTableExists_NoColumnDuplicated`.
- AC-5 (audit log + schema registry populated): `DdlEmitter` adds a `SchemaAuditLogEntry` to the DbContext (committed by the BackgroundService's finally-block `SaveChangesAsync`) AND populates `ISchemaRegistry` with the parsed `ColumnDefinition[]` + child Repeater designerIds. Verified by `ProvisionNewTable_AuditLogRowAppended_OnCreate` and `ProvisionNewTable_SchemaRegistryPopulated_AfterSuccessfulProvisioning`.
- Story 5.2 deferred fix applied: `ProvisioningBackgroundService.ProcessJobAsync` now has an explicit `catch (OperationCanceledException) { throw; }` branch before the catch-all, so host-shutdown mid-DDL no longer records a spurious "Error" status (Story 5.8's recovery scanner will re-enqueue the Pending row on next startup).
- Story 5.2 stub log line `LogProvisioningStub` removed; replaced with `LogProvisioningSucceeded` on the happy path. `LogProvisioningFailed` preserved on the catch-all branch.
- `DbConnectionFactory` exposes a `DdlCommandTimeoutSeconds` constant (60 s) that callers pass via Dapper's per-command timeout parameter. `NpgsqlConnection.CommandTimeout` is read-only at runtime in Npgsql 10 (the story Dev Notes flagged this) so connection-level configuration is not possible without modifying the connection string.
- `ComponentTypeMapper` returns `null` for `Stack`, `Row`, `Tabs`, `Label`, `Button`, `Repeater`, `RepeaterField` (structural / UI-only). Unknown future types fall back to `JSONB`. Note: the FieldKeyValidator's `InputBearingTypes` uses SPA-shaped strings with spaces (`"Text Input"`, `"Number Input"`, `"DateTime Picker"`, `"Color Picker"`, `"Repeater Field"`) while the story spec's `ComponentTypeMapper` uses architecture-shorthand strings without spaces (`"TextInput"`, `"NumberInput"`, etc.). The tests follow the spec's shorthand. The mismatch is a pre-existing inconsistency — production data flowing from the SPA may use the space-separated form and would currently fall through to the `JSONB` fallback. Worth a follow-up bridge mapping or a normalisation step in Story 5.4/5.6 to align the two surfaces; recorded in deferred work below.
- `RootElementParser` walks structural containers (`Stack`, `Row`, `Tabs`) recursively. `Repeater` records its `rowDesignerId` in the second tuple element and does NOT recurse into its children — those belong to a child table provisioned by Story 5.5. Duplicate fieldKeys: first DFS occurrence wins; subsequent duplicates are silently dropped (the SPA's FieldKeyValidator runs at save time and returns a 422 with FIELD_KEY_COLLISION, so live data should never reach the parser with duplicates).
- The Dapper DDL transaction and the EF `SaveChangesAsync` are **separate transactions**. The DDL commits first; if the EF commit fails afterwards, the table exists but `provisioning_status` stays `Pending`. This dual-write hazard is accepted for v1 — Story 5.8's recovery scanner picks up such rows on next startup via the `idx_menus_provisioning_status_pending` filtered index (already in place from Story 5.2).
- Test isolation: `InitializeAsync` now drops every public-schema table that isn't on the static allow-list before each test. This is required because dynamically-provisioned tables persist across tests (TRUNCATE only touches the EF-managed schema). The allow-list includes `schema_audit_log` so the new table introduced by this story isn't dropped.
- Test data note: my integration tests use the spec's anonymous-type shorthand `type = "TextInput"`, `"NumberInput"`, `"Checkbox"`, etc. — matching `ComponentTypeMapper`'s keys. If the SPA evolves to send the SPA-shaped strings with spaces (matching `FieldKeyValidator.InputBearingTypes`), `ComponentTypeMapper` should be updated to accept both surfaces or the SPA should normalise to the shorthand at save time. Recorded as a deferred item below.
- One follow-up identified but **not** applied: ALTER index direction on `idx_schema_audit_log_designer_id_created_at`. EF emitted ascending order; the story Dev Notes said this might need a `migrationBuilder.Sql(...)` to force `created_at DESC`. PostgreSQL can use ASC indexes for `ORDER BY ... DESC LIMIT N` via backward index scans, so for v1 query workloads the direction is not load-bearing. If Story 5.7's audit-log view shows performance regression on large designers, switch to a descending index then.

### Deferred Work

- `ComponentTypeMapper` shorthand-vs-SPA-name surface mismatch. The story spec's mapper uses architecture-shorthand component type strings ("TextInput", "NumberInput", "DateTimePicker", "ColorPicker") which match neither the SPA's actual canvas-stored type strings ("Text Input", "Number Input", "DateTime Picker", "Color Picker" — per `FieldKeyValidator.InputBearingTypes`) nor any other normalised form. As written today, real designer data flowing from the SPA would fall through to the JSONB fallback for every input-bearing component, producing a single JSONB column per field instead of the typed columns the architecture intends. Decision pending: extend `ComponentTypeMapper.MapToPgType` to accept BOTH naming conventions (preferred — bridge mapping) OR normalise types at save time in DesignerService OR change the SPA to emit shorthand. Story 5.4/5.6 should resolve before any production data lands.
- Audit log index direction. `idx_schema_audit_log_designer_id_created_at` is `(designer_id ASC, created_at ASC)`; the story Dev Notes suggested `created_at DESC` for the audit-log view's typical "most-recent-first" query pattern. PostgreSQL backward index scans cover the common case, but a real performance check on Story 5.7's view should re-evaluate.
- Dapper-EF dual-write hazard. The Dapper DDL transaction commits independently of the EF `SaveChangesAsync` that flips menu status to Success + appends the audit log entry. If the second commit fails after the first succeeds, the table exists but the menu row stays Pending forever. Story 5.8's recovery scanner is the documented mitigation — the scanner re-enqueues every Pending row at startup and the idempotent `AddMissingColumnsAsync` makes the second attempt safe.
- IMemoryCache LRU eviction. `SchemaRegistry` uses sliding 1-hour TTL only; no `SizeLimit` + `Size=1` policy is configured. For v1 designer scale (single-digit / low-tens) this is fine. If the designer count grows past ~hundreds and admin churn keeps the cache hot, swap to a bounded-size LRU policy.

### File List

**New files (backend):**
- `src/FormForge.Api/Infrastructure/Persistence/DbConnectionFactory.cs`
- `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260525073718_CreateSchemaAuditLog.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260525073718_CreateSchemaAuditLog.Designer.cs`
- `src/FormForge.Api/Features/SchemaRegistry/ColumnDefinition.cs`
- `src/FormForge.Api/Features/SchemaRegistry/SchemaRegistryEntry.cs`
- `src/FormForge.Api/Features/SchemaRegistry/ComponentTypeMapper.cs`
- `src/FormForge.Api/Features/SchemaRegistry/RootElementParser.cs`
- `src/FormForge.Api/Features/SchemaRegistry/ISchemaRegistry.cs`
- `src/FormForge.Api/Features/SchemaRegistry/SchemaRegistry.cs`
- `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`

**Modified files (backend):**
- `Directory.Packages.props` — added `<PackageVersion Include="Dapper" Version="2.1.66" />`
- `src/FormForge.Api/FormForge.Api.csproj` — added `<PackageReference Include="Dapper" />`
- `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` — added `<PackageReference Include="Dapper" />`
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — added `SchemaAuditLog` DbSet + entity config + indexes
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — auto-regenerated by `dotnet ef migrations add`
- `src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs` — replaced stub body with `DdlEmitter.EmitAsync` call + added OCE re-throw + replaced `LogProvisioningStub` with `LogProvisioningSucceeded`
- `src/FormForge.Api/Program.cs` — added `using FormForge.Api.Features.SchemaRegistry;` + registered `DbConnectionFactory`, `ISchemaRegistry`, `DdlEmitter`

**New files (tests):**
- `src/FormForge.Api.Tests/Features/SchemaRegistry/RootElementParserTests.cs` (+9 unit tests covering empty / field-type mapping / duplicate handling / Repeater isolation / unknown-type JSONB fallback / SafeIdentifier rejection)

**Modified files (tests):**
- `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs` — added `using FormForge.Api.Features.SchemaRegistry;` + `using Npgsql;` + `WebJsonOptions` static + `schema_audit_log` to TRUNCATE list + DROP-loop for dynamic tables + 3 helpers (`CreateAndPublishDesignerWithFieldsAsync<TRoot>`, `TableExistsInPostgresAsync`, `GetTableColumnsAsync`) + 7 new integration tests

### Change Log

| Date | Change |
|---|---|
| 2026-05-25 | Story 5.3 — Provision New Table from Designer Schema. Initial implementation. Backend tests 331 → 347 (+16: 7 integration + 9 unit). Story status: in-progress → review. |

### Review Findings

- [x] [Review][Decision] TOCTOU race — `TableExistsAsync` + `CreateTableAsync` are not atomic; two concurrent jobs for the same `designerId` would both see the table absent and both attempt `CREATE TABLE`; the second fails with a PG error and records `"Error"` status even though the DDL succeeded on the first job — resolved: applied `CREATE TABLE IF NOT EXISTS` [`src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`]

- [x] [Review][Patch] `DbConnectionFactory.CreateOpenConnectionAsync` leaks `NpgsqlConnection` if `OpenAsync` throws — no dispose-on-exception guard around the connection before it is returned to the caller [`src/FormForge.Api/Infrastructure/Persistence/DbConnectionFactory.cs:20-24`]
- [x] [Review][Patch] `RollbackAsync(ct)` in catch blocks uses the user-supplied token — if `ct` is already cancelled the rollback throws immediately and never executes, leaving the transaction unfinished; use `CancellationToken.None` for rollback calls [`src/FormForge.Api/Features/Provisioning/DdlEmitter.cs` CreateTableAsync + AddMissingColumnsAsync catch blocks]
- [x] [Review][Patch] `FromVersion = null` hardcoded on all audit log entries — ALTER path also records `FromVersion = null` which is semantically incorrect (spec AC-5 defines `fromVersion: null` only for CREATE); set `FromVersion = job.Version` on the ALTER path [`src/FormForge.Api/Features/Provisioning/DdlEmitter.cs:112`]
- [x] [Review][Patch] `schemaRegistry.Populate` is unguarded between `connection.DisposeAsync()` and `db.SchemaAuditLog.Add` — if `Populate` throws, the Dapper DDL has committed but no audit entry is added; the BackgroundService catch-all records `"Error"` on the menu even though the table was created successfully [`src/FormForge.Api/Features/Provisioning/DdlEmitter.cs:99-119`]
- [x] [Review][Patch] `"properties": null` in designer JSON causes `InvalidOperationException` in `RootElementParser` — `TryGetProperty` on a Null-kind `JsonElement` throws; add `properties.ValueKind == JsonValueKind.Object` guard after `TryGetProperty("properties", ...)` [`src/FormForge.Api/Features/SchemaRegistry/RootElementParser.cs:52-56, 76-78`]
- [x] [Review][Patch] No recursion depth guard in `RootElementParser.WalkElement` — `FieldKeyValidator` has `MaxDepth = 64` but `RootElementParser` has no limit; adversarially deep JSON causes unrecoverable `StackOverflowException` [`src/FormForge.Api/Features/SchemaRegistry/RootElementParser.cs`]
- [x] [Review][Patch] Dead OCE catch block — `emitter.EmitAsync(job, CancellationToken.None)` makes `OperationCanceledException` unreachable via token cancellation; the comment claiming the Story 5.2 deferred fix is applied is inaccurate; remove or update the comment to reflect the `CancellationToken.None` design intent [`src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs:67-73`]
- [x] [Review][Patch] Stale comment — "Story 5.3 will replace the body of ProcessJobAsync with real DDL via DdlEmitter" — this replacement has already been done [`src/FormForge.Api/Features/Provisioning/ProvisioningBackgroundService.cs`]

- [x] [Review][Defer] ComponentTypeMapper shorthand vs SPA-name mismatch — `FieldKeyValidator.InputBearingTypes` uses spaced strings (`"Text Input"`, `"Number Input"`) but `ComponentTypeMapper` uses shorthand (`"TextInput"`, `"NumberInput"`); real SPA data falls through to JSONB for all typed fields — deferred, pre-existing spec-acknowledged gap (production blocker before Epic 6 CRUD)
- [x] [Review][Defer] `CancellationToken` silently discarded in `TableExistsAsync` and `AddMissingColumnsAsync` (`_ = ct;`) — Dapper calls cannot be cancelled — deferred, pre-existing design choice with no runtime impact since caller passes `CancellationToken.None`
- [x] [Review][Defer] Test helper hardcodes `publishedVersion = 2` — fragile if designer API changes auto-versioning — deferred, pre-existing low-severity test quality item [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`]
- [x] [Review][Defer] `DbConnectionFactory` reads connection string on every `CreateOpenConnectionAsync` call — missing connection string fails at first use, not startup — deferred, pre-existing low-severity
- [x] [Review][Defer] `JsonDocument.Parse` in `RootElementParser` has no `try/catch` for malformed JSON — `JsonException` propagates up and is recorded as `"Error"` status on the menu — deferred, acceptable error-recording behaviour
- [x] [Review][Defer] Idempotent no-op still produces an `"ALTER"` audit row with `ColumnsAdded = []` — could confuse Story 5.7 audit-log view — deferred, spec gap (AC-5 specifies audit log on CREATE only)
- [x] [Review][Defer] Composite index on `(designer_id, created_at)` is ASC/ASC, not DESC on `created_at` — deferred, explicitly acknowledged in Completion Notes pending Story 5.7 workload evaluation
