# Story 5.4: Evolve Schema with a New Designer Version

Status: done

## Story

As the system,
I `ALTER TABLE ... ADD COLUMN` for each new field in an updated Designer version,
So that existing records are never broken by schema changes.

## Acceptance Criteria

**AC-1 — ALTER TABLE on Version Evolution:**
**Given** an updated `{ designerId, version }` binding
**When** the `ProvisioningBackgroundService` consumer processes it and a table already exists
**Then** the provisioner computes the diff between the target version's `RootElement` columns and the live table's column list
**And** new columns are added via `ALTER TABLE ... ADD COLUMN ... NULL` (no default), in a single transaction

**AC-2 — Never Drop Orphaned Columns:**
**Given** the diff computation finds columns in the live table that are absent from the target schema
**Then** those columns are **never** dropped or renamed automatically (FR-25 AC-1)
**And** they are recorded in the `ColumnsDiff` JSON as `orphanedColumns`

**AC-3 — Transaction Rollback on Failure:**
**Given** partial completion of an ALTER
**When** any sub-statement fails
**Then** the entire transaction rolls back — no partial columns are left in PostgreSQL (FR-25 AC-3)

**AC-4 — Audit Log with Correct fromVersion:**
**Given** a successful ALTER
**When** the transaction commits
**Then** a row is appended to `schema_audit_log` with:
- `actorId`, `createdAt`, `correlationId` (ULID)
- `designerId`, `fromVersion` (the **previous** bound version — NOT the new version), `toVersion` (the new version)
- `ddlOperation: "ALTER"`, `columnsAdded: [...]`, `columnsDiff` (JSON with `existingUserColumns`, `addedColumns`, `orphanedColumns`)

**AC-5 — Schema Registry Refreshed for New Version:**
**Given** a successful ALTER
**Then** `ISchemaRegistry.TryGet(designerId, newVersion)` returns the new `SchemaRegistryEntry` with the new version's column definitions

## Tasks / Subtasks

- [x] **Task 1 — Fix `ComponentTypeMapper` SPA-name mismatch** (deferred blocker from Story 5.3)
  - [x] In `src/FormForge.Api/Features/SchemaRegistry/ComponentTypeMapper.cs`, add bridge mappings for the SPA's space-format component type strings. The SPA stores `"Text Input"` etc. in `RootElement` JSON; the current mapper only recognizes shorthand `"TextInput"`, causing production data to fall through to the JSONB fallback.
  - [x] Update `NoColumnTypes` to include `"Repeater Field"` (SPA format for `RepeaterField`)
  - [x] Update `MapToPgType` switch to include all SPA-format names:
    ```csharp
    // SPA format (space-separated) — per FieldKeyValidator.InputBearingTypes
    "Text Input"      => "TEXT",
    "Text Area"       => "TEXT",
    "Color Picker"    => "TEXT",
    "Number Input"    => "NUMERIC",
    "DateTime Picker" => "TIMESTAMPTZ",
    ```
  - [x] **Do NOT remove** the existing shorthand cases — integration tests and the architecture spec use shorthand; both formats must work side-by-side
  - [x] Update `IsImageType` — "Image" has no SPA-format variant; no change needed there

- [x] **Task 2 — Extend `ProvisioningJob` with `FromVersion`** (AC: 4)
  - [x] In `src/FormForge.Api/Features/Provisioning/ProvisioningJob.cs`, add `int? FromVersion = null` as the last positional parameter with a default value so all existing callers compile unchanged:
    ```csharp
    internal sealed record ProvisioningJob(
        Guid MenuId,
        string DesignerId,
        int Version,
        Guid? ActorId,
        int? FromVersion = null);
    ```
  - [x] The default `null` means: first-time CREATE (no previous version) or a Retry (previous version unknown at the retry call site)

- [x] **Task 3 — Update `MenuService.BindDesignerAsync` to pass `FromVersion`** (AC: 4)
  - [x] In `src/FormForge.Api/Features/Menus/MenuService.cs`, capture `menu.BoundVersion` **before** overwriting it, then pass it as `FromVersion` in the enqueued `ProvisioningJob`:
    ```csharp
    var fromVersion = menu.BoundVersion;   // capture BEFORE overwriting — Story 5.4 AC-4
    menu.DesignerId = designerId;
    menu.BoundVersion = version;
    menu.ProvisioningStatus = "Pending";
    ...
    await provisioning
        .EnqueueAsync(
            new ProvisioningJob(menu.Id, designerId, version, actorId, FromVersion: fromVersion),
            CancellationToken.None)
        .ConfigureAwait(false);
    ```
  - [x] `menu.BoundVersion` is `null` on a first-time bind → `fromVersion = null` → `ProvisioningJob.FromVersion = null` → `DdlEmitter` writes `FROM_VERSION = NULL` (CREATE path) ✓
  - [x] `menu.BoundVersion = 1` on a v1→v2 evolution → `fromVersion = 1` → `ProvisioningJob.FromVersion = 1` → `DdlEmitter` writes `FROM_VERSION = 1` (ALTER path) ✓
  - [x] **`RetryBindingAsync` is NOT changed** — retry enqueues with no `FromVersion` (defaults to null). On a retry of an ALTER, the audit log records `fromVersion = null`. This is a known limitation (documented in Dev Notes)

- [x] **Task 4 — Add `ColumnsDiff` to `SchemaAuditLogEntry`** (AC: 4)
  - [x] In `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs`, add after `ColumnsAdded`:
    ```csharp
    public string? ColumnsDiff { get; set; }   // JSON snapshot of diff; null for CREATE
    ```
  - [x] `ColumnsDiff` is `null` for CREATE operations and populated for ALTER operations

- [x] **Task 5 — Update `FormForgeDbContext` entity config for `ColumnsDiff`** (AC: 4)
  - [x] In `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`, add after the `ColumnsAdded` mapping in the `SchemaAuditLogEntry` entity config block:
    ```csharp
    e.Property(a => a.ColumnsDiff).HasColumnName("column_diff");
    ```
  - [x] No `HasColumnType` needed — EF maps `string?` to `TEXT NULL` by default in PostgreSQL

- [x] **Task 6 — Add EF migration `AddColumnDiffToSchemaAuditLog`** (AC: 4)
  - [x] Run: `dotnet ef migrations add AddColumnDiffToSchemaAuditLog --project src/FormForge.Api --startup-project src/FormForge.Api`
  - [x] Verify the generated migration adds `column_diff TEXT NULL` to `schema_audit_log`
  - [x] Add `ArgumentNullException.ThrowIfNull(migrationBuilder)` to both `Up()` and `Down()` per the CA1062 pattern established in `AddMenuBindingColumns` and `CreateSchemaAuditLog`
  - [x] Previous migration: `20260525073718_CreateSchemaAuditLog` — new migration must come after this one
  - [x] **DO NOT** run `dotnet ef database update` manually; the app runs `db.Database.MigrateAsync()` on startup

- [x] **Task 7 — Update `DdlEmitter.cs` for the version-evolution ALTER path** (AC: 1–5)
  - [x] Add `using System.Text.Json;` to the import list (needed for `JsonSerializer.Serialize`)
  - [x] Add a private `AlterResult` record and a `SystemColumnNames` set inside `DdlEmitter`:
    ```csharp
    // System columns created by CreateTableAsync — excluded from orphaned-column tracking.
    private static readonly HashSet<string> SystemColumnNames = new(StringComparer.Ordinal)
    {
        "id", "created_at", "created_by", "updated_at", "updated_by", "is_deleted", "cascade_event_id"
    };

    private sealed record AlterResult(
        string[] ColumnsAdded,
        string[] ExistingUserColumns,
        string[] OrphanedColumns);
    ```
  - [x] Change `AddMissingColumnsAsync` return type from `Task<string[]>` to `Task<AlterResult>`:
    - After fetching `existing` from `information_schema.columns`:
      - `existingUserColumns` = `existing.Where(c => !SystemColumnNames.Contains(c)).ToArray()`
      - `toAdd` = target columns whose `ColumnName` is not in `existing` (unchanged logic)
      - `orphanedColumns` = `existingUserColumns.Where(c => !targetColumnNames.Contains(c)).ToArray()` where `targetColumnNames = columns.Select(c => c.ColumnName).ToHashSet(StringComparer.Ordinal)`
    - Return `new AlterResult([.. toAdd.Select(c => c.ColumnName)], existingUserColumns, orphanedColumns)` instead of `string[]`
    - **No changes** to the actual DDL execution logic — only the returned data changes
  - [x] In `EmitAsync`, update the ALTER branch:
    ```csharp
    AlterResult? alterResult = null;
    string[] columnsAdded;
    // ...
    if (!tableExists)
    {
        columnsAdded = await CreateTableAsync(connection, tableName, columns, ct).ConfigureAwait(false);
        LogCreatedTable(logger, tableName.Value, columnsAdded.Length);
    }
    else
    {
        alterResult = await AddMissingColumnsAsync(connection, tableName, columns, ct).ConfigureAwait(false);
        columnsAdded = alterResult.ColumnsAdded;
        LogAlteredTable(logger, tableName.Value, columnsAdded.Length);
        if (alterResult.OrphanedColumns.Length > 0)
            LogOrphanedColumns(logger, tableName.Value, alterResult.OrphanedColumns.Length,
                job.Version, string.Join(", ", alterResult.OrphanedColumns));
    }
    ```
  - [x] Build the `columnsDiff` JSON immediately after closing the connection (before the audit log add):
    ```csharp
    string? columnsDiffJson = tableExists && alterResult is not null
        ? JsonSerializer.Serialize(new
          {
              existingUserColumns = alterResult.ExistingUserColumns,
              addedColumns        = alterResult.ColumnsAdded,
              orphanedColumns     = alterResult.OrphanedColumns,
          })
        : null;
    ```
  - [x] Update the `SchemaAuditLogEntry` add to use `job.FromVersion` (not `job.Version`) and populate `ColumnsDiff`:
    ```csharp
    db.SchemaAuditLog.Add(new SchemaAuditLogEntry
    {
        DesignerId   = job.DesignerId,
        FromVersion  = tableExists ? job.FromVersion : null,  // Story 5.4: was job.Version (wrong)
        ToVersion    = job.Version,
        DdlOperation = tableExists ? "ALTER" : "CREATE",
        ColumnsAdded = columnsAdded,
        ColumnsDiff  = columnsDiffJson,                        // new field — Story 5.4
        CorrelationId = correlationId,
        ActorId      = job.ActorId,
        CreatedAt    = DateTimeOffset.UtcNow,
    });
    ```
  - [x] Add `LogOrphanedColumns` [LoggerMessage] partial:
    ```csharp
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Table '{TableName}' has {Count} orphaned column(s) absent from designer v{Version}: {Columns}")]
    private static partial void LogOrphanedColumns(
        ILogger logger, string tableName, int count, int version, string columns);
    ```
  - [x] **`CreateTableAsync` is unchanged** — its signature and return type remain `Task<string[]>`

- [x] **Task 8 — Add integration and unit tests** (AC: 1–5 + Task 1 regression)

  **8a — New `ComponentTypeMapperTests.cs`** (`src/FormForge.Api.Tests/Features/SchemaRegistry/ComponentTypeMapperTests.cs`):
  ```csharp
  using FormForge.Api.Features.SchemaRegistry;

  namespace FormForge.Api.Tests.Features.SchemaRegistry;

  public sealed class ComponentTypeMapperTests
  {
      [Theory]
      [InlineData("TextInput",       "TEXT")]
      [InlineData("TextArea",        "TEXT")]
      [InlineData("Dropdown",        "TEXT")]
      [InlineData("ColorPicker",     "TEXT")]
      [InlineData("Image",           "TEXT")]
      [InlineData("NumberInput",     "NUMERIC")]
      [InlineData("Checkbox",        "BOOLEAN")]
      [InlineData("DateTimePicker",  "TIMESTAMPTZ")]
      // SPA-format bridge mappings (Story 5.4)
      [InlineData("Text Input",      "TEXT")]
      [InlineData("Text Area",       "TEXT")]
      [InlineData("Color Picker",    "TEXT")]
      [InlineData("Number Input",    "NUMERIC")]
      [InlineData("DateTime Picker", "TIMESTAMPTZ")]
      public void MapToPgType_InputComponents_ReturnsExpectedPgType(
          string componentType, string expectedPgType)
          => Assert.Equal(expectedPgType, ComponentTypeMapper.MapToPgType(componentType));

      [Theory]
      [InlineData("Stack")]
      [InlineData("Row")]
      [InlineData("Tabs")]
      [InlineData("Label")]
      [InlineData("Button")]
      [InlineData("Repeater")]
      [InlineData("RepeaterField")]
      [InlineData("Repeater Field")]   // SPA format (Story 5.4)
      public void MapToPgType_StructuralTypes_ReturnsNull(string componentType)
          => Assert.Null(ComponentTypeMapper.MapToPgType(componentType));

      [Fact]
      public void MapToPgType_UnknownType_ReturnsJsonb()
          => Assert.Equal("JSONB", ComponentTypeMapper.MapToPgType("FutureWidget"));
  }
  ```

  **8b — New integration tests in `ProvisioningIntegrationTests.cs`** (+5 [Fact] methods):

  ```csharp
  [Fact]
  public async Task EvolveSchema_NewVersionWithAdditionalField_ColumnAdded()
  {
      // AC-1: binding to a newer version that has an extra field adds the column.
      var token = await LoginAsync("admin@example.com", "Password1!");
      var menuId = await CreateMenuViaApiAsync(token, "EvolveMenu1", 0);

      // v2 has first_name, last_name
      var rootV1 = new { id = "root", type = "Stack", properties = new { }, children = new object[]
      {
          new { id = "f1", type = "TextInput", properties = new { fieldKey = "first_name" },
                children = Array.Empty<object>() },
          new { id = "f2", type = "TextInput", properties = new { fieldKey = "last_name" },
                children = Array.Empty<object>() },
      }};
      var v1 = await CreateAndPublishDesignerWithFieldsAsync<object>(token, "evolve_schema_1", rootV1);
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
      // Post v3 (CreateAndPublishDesignerWithFieldsAsync creates v1+v2; next is v3)
      var contentV2 = $"{{\"rootElement\":{JsonSerializer.Serialize(rootV2, WebJsonOptions)}}}";
      using (var postReq = new HttpRequestMessage(HttpMethod.Post, "/api/designers/evolve_schema_1/versions")
          { Content = new System.Net.Http.StringContent(contentV2, System.Text.Encoding.UTF8, "application/json") })
      {
          postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
          (await _client!.SendAsync(postReq)).EnsureSuccessStatusCode();
      }
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
      var v1 = await CreateAndPublishDesignerWithFieldsAsync<object>(token, "evolve_schema_2", rootWithTwo);
      using (var bind1 = await PutBindingAsync(token, menuId, "evolve_schema_2", v1))
          Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
      Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

      // v3 removes orphan_col from the schema
      var rootWithOne = new { id = "root", type = "Stack", properties = new { }, children = new object[]
      {
          new { id = "f1", type = "TextInput", properties = new { fieldKey = "keep_col" },
                children = Array.Empty<object>() },
      }};
      var contentV2 = $"{{\"rootElement\":{JsonSerializer.Serialize(rootWithOne, WebJsonOptions)}}}";
      using (var postReq = new HttpRequestMessage(HttpMethod.Post, "/api/designers/evolve_schema_2/versions")
          { Content = new System.Net.Http.StringContent(contentV2, System.Text.Encoding.UTF8, "application/json") })
      {
          postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
          (await _client!.SendAsync(postReq)).EnsureSuccessStatusCode();
      }
      await PutVersionStatusAsync(token, "evolve_schema_2", 3, "Published");

      using (var bind2 = await PutBindingAsync(token, menuId, "evolve_schema_2", 3))
          Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
      Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

      var cols = await GetTableColumnsAsync("evolve_schema_2");
      var colNames = cols.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
      Assert.Contains("keep_col", colNames);
      Assert.Contains("orphan_col", colNames);   // must NOT be dropped (AC-2)
      Assert.Equal(9, cols.Count);               // 7 system + 2 user columns (keep_col + orphan_col)
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
      var v1 = await CreateAndPublishDesignerWithFieldsAsync<object>(token, "evolve_schema_3", rootV1);
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
      var contentV2 = $"{{\"rootElement\":{JsonSerializer.Serialize(rootV2, WebJsonOptions)}}}";
      using (var postReq = new HttpRequestMessage(HttpMethod.Post, "/api/designers/evolve_schema_3/versions")
          { Content = new System.Net.Http.StringContent(contentV2, System.Text.Encoding.UTF8, "application/json") })
      {
          postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
          (await _client!.SendAsync(postReq)).EnsureSuccessStatusCode();
      }
      await PutVersionStatusAsync(token, "evolve_schema_3", 3, "Published");

      using (var bind2 = await PutBindingAsync(token, menuId, "evolve_schema_3", 3))
          Assert.Equal(HttpStatusCode.Accepted, bind2.StatusCode);
      Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

      await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
      await conn.OpenAsync();
      using var cmd = conn.CreateCommand();
      cmd.CommandText = """
          SELECT ddl_operation, from_version, to_version, columns_added, column_diff
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
      // column_diff is JSON TEXT — verify it is non-null and parseable
      var columnDiff = reader.GetString(4);
      Assert.False(string.IsNullOrWhiteSpace(columnDiff));
      using var diffDoc = JsonDocument.Parse(columnDiff);
      Assert.True(diffDoc.RootElement.TryGetProperty("addedColumns", out _));
      Assert.True(diffDoc.RootElement.TryGetProperty("orphanedColumns", out _));
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
      var v1 = await CreateAndPublishDesignerWithFieldsAsync<object>(token, "evolve_schema_4", rootV1);
      using (var bind1 = await PutBindingAsync(token, menuId, "evolve_schema_4", v1))
          Assert.Equal(HttpStatusCode.Accepted, bind1.StatusCode);
      Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

      var rootV2 = new { id = "root", type = "Stack", properties = new { }, children = new object[]
          { new { id = "f1", type = "TextInput", properties = new { fieldKey = "val" },
                  children = Array.Empty<object>() },
            new { id = "f2", type = "NumberInput", properties = new { fieldKey = "qty" },
                  children = Array.Empty<object>() } }};
      var contentV2 = $"{{\"rootElement\":{JsonSerializer.Serialize(rootV2, WebJsonOptions)}}}";
      using (var postReq = new HttpRequestMessage(HttpMethod.Post, "/api/designers/evolve_schema_4/versions")
          { Content = new System.Net.Http.StringContent(contentV2, System.Text.Encoding.UTF8, "application/json") })
      {
          postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
          (await _client!.SendAsync(postReq)).EnsureSuccessStatusCode();
      }
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
      await CreateAndPublishDesignerWithFieldsAsync<object>(token, "spa_format_test", rootElement);

      using (var bind = await PutBindingAsync(token, menuId, "spa_format_test", 2))
          Assert.Equal(HttpStatusCode.Accepted, bind.StatusCode);
      Assert.Equal("Success", await PollUntilTerminalAsync(token, menuId, TimeSpan.FromSeconds(10)));

      var cols = await GetTableColumnsAsync("spa_format_test");
      var colMap = cols.ToDictionary(c => c.Name, c => c.DataType, StringComparer.Ordinal);
      Assert.Equal("text",                    colMap["full_name"]);  // Text Input → TEXT (not JSONB)
      Assert.Equal("numeric",                 colMap["age"]);        // Number Input → NUMERIC
      Assert.Equal("timestamp with time zone",colMap["dob"]);        // DateTime Picker → TIMESTAMPTZ
      Assert.Equal("text",                    colMap["color"]);      // Color Picker → TEXT
  }
  ```

  - [x] **Test baseline: 347** (end of Story 5.3 code review)
  - [x] **Estimated additions:** +22 unit tests (`ComponentTypeMapperTests.cs` — 3 methods with InlineData) + 5 integration tests = **+27**
  - [x] **Estimated total: 374**
  - [x] All 347 existing tests must still pass

## Dev Notes

### What Story 5.4 Adds vs. What Story 5.3 Already Does

Story 5.3's AC-4 (idempotent fallthrough) already implemented `AddMissingColumnsAsync` which queries live columns and runs ALTER TABLE ADD COLUMN for missing ones. **Story 5.4 reuses this exact DDL logic** — the version evolution path goes through the same code.

**What Story 5.4 adds on top:**
1. `job.FromVersion` so the audit log records the correct previous version (not `job.Version` as it currently does — that was a bug)
2. `ColumnsDiff` JSON capturing `existingUserColumns`, `addedColumns`, `orphanedColumns`
3. `ComponentTypeMapper` SPA-name bridge mappings (production blocker from deferred work)

**What is NOT changing:**
- The DDL transaction logic inside `AddMissingColumnsAsync`
- `CreateTableAsync` — untouched
- `ProvisioningBackgroundService` — untouched (already calls `emitter.EmitAsync(job, CancellationToken.None)`)
- The bind/retry HTTP endpoints — untouched
- Frontend — this story is backend-only

### Critical: FromVersion Bug in Current Code

The current `DdlEmitter` has:
```csharp
FromVersion = tableExists ? job.Version : null,
```

This sets `FromVersion = toVersion` for the ALTER path — e.g., if binding v3 and the table exists, it records `FromVersion = 3, ToVersion = 3`. That's wrong. Story 5.4 fixes it to:
```csharp
FromVersion = tableExists ? job.FromVersion : null,
```

`job.FromVersion` is `null` if the caller didn't pass it (first-time bind via `BindDesignerAsync` when `menu.BoundVersion = null`, or via `RetryBindingAsync` which always passes `null`). For an evolution bind (v1→v2), `BindDesignerAsync` now passes `menu.BoundVersion` (v1) as `job.FromVersion`.

### RetryBindingAsync Known Limitation

`RetryBindingAsync` (in `MenuService.cs`) enqueues a job with default `FromVersion = null`. This means:
- A retry where the table already exists → `ddlOperation = "ALTER"`, `fromVersion = null` in the audit log
- This is slightly misleading (the true "from" is the first provision version), but acceptable for v1
- Tracking the true fromVersion through retries would require querying `schema_audit_log` for the last successful CREATE, which is over-engineered for this story

### `AddMissingColumnsAsync` Refactoring — Precise Change

Current method body (simplified):
```csharp
var existing = (await QueryAsync<string>(...)).ToHashSet(StringComparer.Ordinal);
var toAdd = columns.Where(c => !existing.Contains(c.ColumnName)).ToList();
if (toAdd.Count == 0) return [];
// ... DDL transaction ...
return [.. toAdd.Select(c => c.ColumnName)];
```

After Story 5.4:
```csharp
var existing = (await QueryAsync<string>(...)).ToHashSet(StringComparer.Ordinal);
var targetColumnNames = columns.Select(c => c.ColumnName).ToHashSet(StringComparer.Ordinal);
var existingUserColumns = existing.Where(c => !SystemColumnNames.Contains(c)).ToArray();
var orphanedColumns = existingUserColumns.Where(c => !targetColumnNames.Contains(c)).ToArray();
var toAdd = columns.Where(c => !existing.Contains(c.ColumnName)).ToList();
if (toAdd.Count == 0)
    return new AlterResult([], existingUserColumns, orphanedColumns);  // no-op still returns diff info
// ... DDL transaction (unchanged) ...
return new AlterResult([.. toAdd.Select(c => c.ColumnName)], existingUserColumns, orphanedColumns);
```

The key change is the no-op path now returns `new AlterResult([], ..., ...)` instead of `return []`, so `EmitAsync` can still compute `columnsDiff` even when no columns were added.

### ColumnsDiff JSON Format

The `columnsDiff` is serialized with default `JsonSerializer.Serialize` options (PascalCase property names). Example for a v1→v2 evolution (adds `email`, `phone` was removed from schema but left in table):
```json
{
  "existingUserColumns": ["first_name", "last_name", "phone"],
  "addedColumns": ["email"],
  "orphanedColumns": ["phone"]
}
```

For Story 5.7's audit-log view, read this JSON from the `column_diff` TEXT column and deserialize as needed.

### EF Migration Pattern

All EF migrations in this project add `ArgumentNullException.ThrowIfNull(migrationBuilder)` to both `Up()` and `Down()` per CA1062. The generated migration will need this added manually after running `dotnet ef migrations add`.

### Test Version Number Assumptions

The integration tests in this story follow the same pattern established in Story 5.3:
- `CreateAndPublishDesignerWithFieldsAsync<TRoot>` creates v1 (null-root) + v2 (custom root) → always returns 2
- Tests that evolve to v3 hardcode version 3 for the next POST
- This is a known fragile pattern (deferred from Story 5.3) — acceptable for now

**For the `EvolveSchema_AuditLogRecordsAlterWithCorrectFromAndToVersion` test:**
- `v1` variable holds the return value of `CreateAndPublishDesignerWithFieldsAsync` = 2
- The evolution target is always version 3
- The audit log assertion is `Assert.Equal(v1, reader.GetInt32(1))` where `v1 = 2` — this verifies that `FromVersion = 2` not `FromVersion = 3`

### ComponentTypeMapper — SPA Names Source of Truth

The SPA-format names come directly from `FieldKeyValidator.InputBearingTypes`:
- `"Text Input"` → was `"TextInput"` in ComponentTypeMapper
- `"Number Input"` → was `"NumberInput"`
- `"DateTime Picker"` → was `"DateTimePicker"`
- `"Color Picker"` → was `"ColorPicker"`
- `"Repeater Field"` → was `"RepeaterField"` (structural, maps to null)

`TextArea` → `"Text Area"` is not in FieldKeyValidator (not input-bearing in the same sense), but added defensively since the SPA likely stores it with a space.

`Dropdown`, `Image`, `Checkbox` have no known SPA-format variant — not added.

### Files to Read Before Writing Any Code

These files are MODIFIED by Story 5.4 — read them completely first:

1. `src/FormForge.Api/Features/SchemaRegistry/ComponentTypeMapper.cs` — add SPA names
2. `src/FormForge.Api/Features/Provisioning/ProvisioningJob.cs` — add `FromVersion` parameter
3. `src/FormForge.Api/Features/Menus/MenuService.cs` — `BindDesignerAsync` around line 434
4. `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs` — add `ColumnsDiff`
5. `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — `SchemaAuditLogEntry` entity config
6. `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs` — refactor `AddMissingColumnsAsync`
7. `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs` — add tests

### Project Structure Notes

- `ComponentTypeMapperTests.cs` goes in `src/FormForge.Api.Tests/Features/SchemaRegistry/` — a folder that already exists (Story 5.3 added `RootElementParserTests.cs` there)
- `AlterResult` and `SystemColumnNames` are **private** to `DdlEmitter` — not exposed in any interface
- `ColumnsDiff` is a property on the domain entity `SchemaAuditLogEntry` — not a separate type; the JSON string is stored as TEXT in PG

### References

- **Decision 1.2** (Component → PG type mapping): `_bmad-output/planning-artifacts/architecture.md`
- **Decision 1.5** (audit log indexes): `architecture.md`
- **Decision 1.6** (EF/Dapper transaction boundary): `architecture.md`
- **FR-25 AC-1** (never drop orphaned columns): `_bmad-output/planning-artifacts/prd.md`
- **FieldKeyValidator.InputBearingTypes** (SPA component type names): `src/FormForge.Api/Features/Designer/FieldKeyValidator.cs`
- **ComponentTypeMapper shorthand deferred issue**: `_bmad-output/implementation-artifacts/deferred-work.md`
- **Story 5.3 DdlEmitter** (code being extended): `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`
- **Story 5.3 ProvisioningIntegrationTests** (test pattern to follow): `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`
- **Previous story file**: `_bmad-output/implementation-artifacts/5-3-provision-new-table-from-designer-schema.md`

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

- Build #1 failed CA1873: `string.Join(", ", alterResult.OrphanedColumns)` inside the `LogOrphanedColumns` call was evaluated unconditionally. Wrapped the call in an `if (logger.IsEnabled(LogLevel.Information))` block; the analyzer still flagged the inner expression even when combined into a single `&&` guard, so the final form is an outer `if (... && IsEnabled)` with a localised `#pragma warning disable CA1873` around the source-generated LoggerMessage helper. Audit-log JSON payload (built immediately after) always captures the full `orphanedColumns` array regardless of log level — guard is purely a log-line cost optimisation.
- `dotnet ef migrations add AddColumnDiffToSchemaAuditLog` auto-regenerated both `20260525104940_AddColumnDiffToSchemaAuditLog.Designer.cs` and `FormForgeDbContextModelSnapshot.cs`. The generated `Up()`/`Down()` did not include `ArgumentNullException.ThrowIfNull(migrationBuilder)` so both were added manually to match the CA1062 pattern established by `AddMenuBindingColumns` and `CreateSchemaAuditLog`.
- Test build initially failed because the new integration tests called a `PostVersionWithRootAsync` helper that did not yet exist on the test class — added as a sibling to `PostVersionAsync` using the same `JsonSerializer.Serialize → raw StringContent` workaround established by `CreateAndPublishDesignerWithFieldsAsync` (object-typed members collapse to `{}` via `JsonContent.Create`'s polymorphic resolution).

### Completion Notes List

- Story 5.4 status: ready-for-dev → review
- All 5 ACs implemented:
  - AC-1 (ALTER TABLE on evolution): `DdlEmitter.AddMissingColumnsAsync` reused from Story 5.3 + new orphan/diff computation; emits `ALTER TABLE ... ADD COLUMN ... NULL` per missing column in a single transaction.
  - AC-2 (Never drop orphans): `orphanedColumns` recorded in `ColumnsDiff` JSON, never used in DDL. Integration test `EvolveSchema_OrphanedColumnsRetained_NeverDropped` asserts the orphan column remains after evolution to a designer that excludes it.
  - AC-3 (Rollback on failure): unchanged from Story 5.3 — the existing try/CommitAsync/catch/RollbackAsync(CancellationToken.None)/finally/DisposeAsync block in `AddMissingColumnsAsync` already covers this.
  - AC-4 (Audit log fromVersion): fixed the bug where `FromVersion = job.Version` (i.e. fromVersion = toVersion) — changed to `job.FromVersion`. MenuService.BindDesignerAsync now captures `menu.BoundVersion` BEFORE the overwrite and passes it as `ProvisioningJob.FromVersion`. New `ColumnsDiff` JSON property added to `SchemaAuditLogEntry`.
  - AC-5 (Schema registry): unchanged from Story 5.3 — `schemaRegistry.Populate` is called at the end of `EmitAsync` for every successful provision regardless of CREATE/ALTER path.
- Production blocker from Story 5.3 deferred-work resolved: `ComponentTypeMapper` now bridges SPA-format strings ("Text Input", "Number Input", "DateTime Picker", "Color Picker", "Repeater Field") alongside the existing shorthand cases. Both formats work side-by-side; the shorthand is preserved for the architecture spec and existing tests.
- RetryBindingAsync is intentionally NOT changed — it enqueues with default `FromVersion = null`, so a retry of an ALTER records `fromVersion = null` in the audit log. Documented in story Dev Notes as a known limitation acceptable for v1.
- Tests: 347 (end of Story 5.3) → 374 (+27, exactly matches story estimate). New `ComponentTypeMapperTests.cs` adds 22 unit tests (3 [Theory] methods with InlineData expanding to 13 + 8 + 1 = 22). 5 new integration tests added to `ProvisioningIntegrationTests.cs` covering AC-1 / AC-2 / AC-4 / AC-5 / Task 1 SPA-format regression. All 374 tests pass.
- Build: dotnet build clean (0 warnings, 0 errors) on full solution.
- Migration: new EF migration `20260525104940_AddColumnDiffToSchemaAuditLog` adds `column_diff TEXT NULL` to `schema_audit_log`. `db.Database.MigrateAsync()` runs on startup per the existing pattern; not manually applied.

### File List

**Modified files (backend):**
- `src/FormForge.Api/Features/SchemaRegistry/ComponentTypeMapper.cs`
- `src/FormForge.Api/Features/Provisioning/ProvisioningJob.cs`
- `src/FormForge.Api/Features/Menus/MenuService.cs`
- `src/FormForge.Api/Domain/Entities/SchemaAuditLogEntry.cs`
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` (auto-regenerated)
- `src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`

**New files (backend):**
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260525104940_AddColumnDiffToSchemaAuditLog.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260525104940_AddColumnDiffToSchemaAuditLog.Designer.cs`

**New files (tests):**
- `src/FormForge.Api.Tests/Features/SchemaRegistry/ComponentTypeMapperTests.cs`

**Modified files (tests):**
- `src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`

### Change Log

| Date | Change |
|---|---|
| 2026-05-25 | Story 5.4 created — ready-for-dev |
| 2026-05-25 | Story 5.4 implemented — ready-for-dev → review. ALTER-path evolution with orphan retention, ColumnsDiff JSON, fromVersion fix, and ComponentTypeMapper SPA-name bridge. 27 new tests (347 → 374), build clean, all tests pass. |
| 2026-05-25 | Code review run — 1 decision-needed, 4 patches, 8 deferred, 5 dismissed. See Review Findings below. |

### Review Findings

- [x] [Review][Patch] Add `"Text Area"` to `FieldKeyValidator.InputBearingTypes` [`src/FormForge.Api/Features/Designer/FieldKeyValidator.cs`] — SPA emits `"Text Area"` (spaced). ComponentTypeMapper correctly maps it → TEXT (Story 5.4 bridge), but FieldKeyValidator.InputBearingTypes only contains `"TextArea"` (no space), so fieldKey is not required at designer save time for Text Area components. A saved Text Area without a fieldKey silently produces no column at provision time. Fix: add `"Text Area"` to the `InputBearingTypes` set.

- [x] [Review][Patch] AC-4 audit test does not assert `existingUserColumns` in column_diff JSON [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — `EvolveSchema_AuditLogRecordsAlterWithCorrectFromAndToVersion` checks `addedColumns` and `orphanedColumns` via `TryGetProperty` but never asserts presence of `existingUserColumns`, which is part of the AC-4 columnsDiff spec.
- [x] [Review][Patch] AC-4 audit test does not assert `actorId` or `correlationId` on the ALTER audit row [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — Story 5.3's CREATE audit test asserts both; the Story 5.4 ALTER test SELECT omits `actor_id` and `correlation_id` columns entirely.
- [x] [Review][Patch] AC-2 `orphanedColumns` asserted by presence only, not by content [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — `EvolveSchema_AuditLogRecordsAlterWithCorrectFromAndToVersion` only calls `TryGetProperty("orphanedColumns", out _)`. No test verifies which column names appear in the array. A bug that serialized an empty `orphanedColumns` array would pass.
- [x] [Review][Patch] SPA-format test discards return value and hardcodes version 2 [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — `EvolveSchema_SpaFormatComponentTypes_MapToCorrectPgTypes` calls `await CreateAndPublishDesignerWithFieldsAsync(...)` without capturing the return value, then binds to hardcoded version `2`. Use `var publishedVersion = await CreateAndPublishDesignerWithFieldsAsync(...)` and pass `publishedVersion` to `PutBindingAsync`.

- [x] [Review][Defer] Hardcoded version `3` in all four evolution tests [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — deferred, pre-existing. Known fragile pattern from Story 5.3 (`CreateAndPublishDesignerWithFieldsAsync` hardcodes `publishedVersion = 2`; callers hardcode `3` for next version). Tracked in existing deferred item.
- [x] [Review][Defer] `existingUserColumns` serialization order non-deterministic (built from `HashSet<string>`) [`src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`] — deferred, pre-existing. Cosmetic for a JSON diff blob; no functional impact unless a consumer hashes the field.
- [x] [Review][Defer] `LIMIT 1` audit query in `EvolveSchema_AuditLogRecordsAlterWithCorrectFromAndToVersion` has no serial isolation guard [`src/FormForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — deferred, pre-existing. Pre-existing test-class pattern; within-class xUnit serial execution mitigates the race.
- [x] [Review][Defer] Retry ALTER writes `from_version = NULL` — indistinguishable from CREATE in audit log [`src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`] — deferred, pre-existing. Explicitly documented known limitation in story Dev Notes; RetryBindingAsync intentionally unchanged.
- [x] [Review][Defer] `SystemColumnNames` has no compile-time link to `CreateTableAsync` SQL [`src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`] — deferred, pre-existing. A future story adding a system column to the dynamic-table DDL without updating this set will cause all subsequent ALTER diffs to show it as orphaned. Maintenance trap with no current safety net.
- [x] [Review][Defer] `existingUserColumns` snapshot computed outside the DDL transaction [`src/FormForge.Api/Features/Provisioning/DdlEmitter.cs`] — deferred, pre-existing. A concurrent recovery-scanner + retry race (Story 5.8 scope) can commit columns between the `information_schema` read and the ALTER commit, leaving the audit diff JSON describing a stale pre-race column set.
- [x] [Review][Defer] AC-3 rollback-on-failure integration test absent — deferred, pre-existing. No test injects a mid-ALTER failure and asserts no partial columns remain. The DDL rollback path is inherited unchanged from Story 5.3 which also had no explicit rollback test.
- [x] [Review][Defer] CREATE path `column_diff IS NULL` not asserted in `ProvisionNewTable_AuditLogRowAppended_OnCreate` — deferred, pre-existing. The Story 5.3 test predates this field; adding the assertion is a Story 5.3 test-backfill, not a Story 5.4 regression.
