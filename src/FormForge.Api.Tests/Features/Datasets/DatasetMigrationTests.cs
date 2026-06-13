using System.Diagnostics.CodeAnalysis;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.Datasets;

// Story 8.1 (FR-55, AR-57, AR-58) — verifies the CreateDatasetManagerFoundation
// migration against a real PostgreSQL (Testcontainers, no mocking). The migration is
// applied once via Database.MigrateAsync() in InitializeAsync; each test then inspects
// the resulting schema through information_schema / pg_catalog or exercises the CHECK
// constraint. A second MigrateAsync() call asserts EF idempotency (AC-5).
[Collection("DatasetIntegrationTests")]
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class DatasetMigrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public DatasetMigrationTests(PostgresFixture postgres) => _postgres = postgres;

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
        // AC-5: applying the migration on a fresh database succeeds...
        await db.Database.MigrateAsync();
        // ...and a second application is a no-op (EF migration-history idempotency).
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task DatasetsSchema_Exists()
    {
        // AC-1: the `datasets` schema is created for later Dataset VIEW DDL.
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name = 'datasets';",
            conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal("datasets", result);
    }

    [Fact]
    public async Task CustomDataset_HasExpectedColumns()
    {
        // AC-2: custom_dataset has all columns with the correct PostgreSQL types.
        var columns = await GetColumnsAsync("custom_dataset");

        AssertColumn(columns, "id", "uuid", nullable: false);
        AssertColumn(columns, "dataset_name", "text", nullable: false);
        AssertColumn(columns, "is_custom_query", "boolean", nullable: false);
        AssertColumn(columns, "query", "text", nullable: true);
        AssertColumn(columns, "builder_state", "jsonb", nullable: true);
        AssertColumn(columns, "version", "integer", nullable: false);
        // created_at is NOT NULL with DEFAULT now() — matches every other table's
        // created_at convention (non-nullable DateTimeOffset + HasDefaultValueSql).
        AssertColumn(columns, "created_at", "timestamp with time zone", nullable: false);
        AssertColumn(columns, "created_by", "uuid", nullable: true);
        AssertColumn(columns, "updated_at", "timestamp with time zone", nullable: true);
        AssertColumn(columns, "updated_by", "uuid", nullable: true);
    }

    [Fact]
    public async Task DatasetAuditLog_HasExpectedColumns()
    {
        // AC-3: dataset_audit_log has all columns with the correct PostgreSQL types.
        var columns = await GetColumnsAsync("dataset_audit_log");

        AssertColumn(columns, "id", "uuid", nullable: false);
        // timestamp is NOT NULL with DEFAULT now() — same convention as created_at.
        AssertColumn(columns, "timestamp", "timestamp with time zone", nullable: false);
        AssertColumn(columns, "actor_id", "uuid", nullable: true);
        AssertColumn(columns, "actor_name", "text", nullable: true);
        AssertColumn(columns, "dataset_name", "text", nullable: false);
        AssertColumn(columns, "operation", "text", nullable: false);
        AssertColumn(columns, "previous_values", "jsonb", nullable: true);
        AssertColumn(columns, "new_values", "jsonb", nullable: true);
        AssertColumn(columns, "ddl", "text", nullable: true);
        AssertColumn(columns, "succeeded", "boolean", nullable: false);
        AssertColumn(columns, "correlation_id", "text", nullable: true);
    }

    [Fact]
    public async Task DatasetAuditLog_OperationCheckConstraint_RejectsInvalidValue()
    {
        // AC-3: the operation CHECK constraint rejects values outside the allowed set.
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO dataset_audit_log (dataset_name, operation) VALUES ('ds_x', 'BOGUS');",
            conn);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_dataset_audit_log_operation", ex.ConstraintName);
    }

    [Theory]
    [InlineData("CREATE")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task DatasetAuditLog_OperationCheckConstraint_AcceptsValidValues(string operation)
    {
        // AC-3: the allowed operation values insert successfully.
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO dataset_audit_log (dataset_name, operation) VALUES ('ds_valid', @op);",
            conn);
        cmd.Parameters.AddWithValue("op", operation);

        var rows = await cmd.ExecuteNonQueryAsync();
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Roles_PlatformAdmin_CanManageDatasetsIsTrue()
    {
        // AC-4: the seeded platform-admin role is granted can_manage_datasets.
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT can_manage_datasets FROM roles WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", PlatformAdminRoleId);

        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(true, result);
    }

    [Fact]
    public async Task Roles_NewlyInsertedRole_CanManageDatasetsDefaultsFalse()
    {
        // AC-4: a freshly-inserted role row defaults can_manage_datasets to false.
        await using var conn = await OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO roles (name) VALUES ('dataset-default-probe') RETURNING can_manage_datasets;",
            conn);

        var result = await insert.ExecuteScalarAsync();
        Assert.Equal(false, result);
    }

    [Fact]
    public async Task Indexes_AreCreated()
    {
        // AC-7: the three Dataset indexes exist, with the dataset_name index unique.
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT i.indexname, ix.indisunique
            FROM pg_indexes i
            JOIN pg_class c ON c.relname = i.indexname
            JOIN pg_index ix ON ix.indexrelid = c.oid
            WHERE i.schemaname = 'public'
              AND i.indexname IN (
                  'idx_custom_dataset_dataset_name',
                  'idx_dataset_audit_log_dataset_name_timestamp',
                  'idx_dataset_audit_log_operation');
            """,
            conn);

        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                found[reader.GetString(0)] = reader.GetBoolean(1);
            }
        }

        Assert.True(found.ContainsKey("idx_custom_dataset_dataset_name"), "unique dataset_name index missing");
        Assert.True(found["idx_custom_dataset_dataset_name"], "idx_custom_dataset_dataset_name should be UNIQUE");
        Assert.Contains("idx_dataset_audit_log_dataset_name_timestamp", found.Keys);
        Assert.Contains("idx_dataset_audit_log_operation", found.Keys);
    }

    [Fact]
    public async Task CustomDataset_DatasetNameUniqueConstraint_RejectsDuplicate()
    {
        // AC-2: the UNIQUE index on dataset_name is a second line of defence.
        await using var conn = await OpenAsync();
        await using (var first = new NpgsqlCommand(
            "INSERT INTO custom_dataset (dataset_name) VALUES ('dupe_probe');", conn))
        {
            await first.ExecuteNonQueryAsync();
        }

        await using var second = new NpgsqlCommand(
            "INSERT INTO custom_dataset (dataset_name) VALUES ('dupe_probe');", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task PreviewRole_Exists()
    {
        // Decision 6.7: the read-only formforge_preview role is created by the migration.
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_roles WHERE rolname = 'formforge_preview';", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task PreviewRole_Users_HasColumnLevelSelect_NotSensitiveColumns()
    {
        // Migration RestrictPreviewRoleUsersColumns grants the preview role SELECT on only the
        // identity columns of `users`; sensitive columns stay denied. has_column_privilege is
        // the authoritative check (a column-level grant does NOT show as a table privilege).
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT has_column_privilege('formforge_preview', 'public.users', 'id', 'SELECT'),
                   has_column_privilege('formforge_preview', 'public.users', 'display_name', 'SELECT'),
                   has_column_privilege('formforge_preview', 'public.users', 'email', 'SELECT'),
                   has_column_privilege('formforge_preview', 'public.users', 'is_active', 'SELECT'),
                   has_column_privilege('formforge_preview', 'public.users', 'password_hash', 'SELECT'),
                   has_column_privilege('formforge_preview', 'public.users', 'mfa_secret_protected', 'SELECT');
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.True(reader.GetBoolean(0), "id should be readable");
        Assert.True(reader.GetBoolean(1), "display_name should be readable");
        Assert.True(reader.GetBoolean(2), "email should be readable");
        Assert.True(reader.GetBoolean(3), "is_active should be readable");
        Assert.False(reader.GetBoolean(4), "password_hash must NOT be readable");
        Assert.False(reader.GetBoolean(5), "mfa_secret_protected must NOT be readable");
    }

    // ---------- helpers ----------

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<IReadOnlyDictionary<string, (string DataType, bool Nullable)>> GetColumnsAsync(string table)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table;
            """,
            conn);
        cmd.Parameters.AddWithValue("table", table);

        var result = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var dataType = reader.GetString(1);
            var nullable = string.Equals(reader.GetString(2), "YES", StringComparison.Ordinal);
            result[name] = (dataType, nullable);
        }

        return result;
    }

    private static void AssertColumn(
        IReadOnlyDictionary<string, (string DataType, bool Nullable)> columns,
        string name,
        string expectedType,
        bool nullable)
    {
        Assert.True(columns.ContainsKey(name), $"column '{name}' is missing");
        var (dataType, isNullable) = columns[name];
        Assert.Equal(expectedType, dataType);
        Assert.Equal(nullable, isNullable);
    }
}
