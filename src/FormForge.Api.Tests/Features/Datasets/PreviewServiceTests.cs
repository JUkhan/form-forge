using System.Text.Json;
using FormForge.Api.Features.Datasets;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FormForge.Api.Tests.Features.Datasets;

// Story 11.3 (FR-72 / AR-63) — unit coverage for PreviewService's SQL-resolution and
// error-classification branches that do NOT require a live database connection. The DB
// execution path (200 + LIMIT 10) is exercised end-to-end in DatasetPreviewTests; here we
// stub IPreviewConnectionFactory so the resolve-and-classify logic is asserted in isolation.
// The timeout (57014) detection is verified by faulting the connection factory with a
// PostgresException carrying that SqlState (the real timeout-via-pg_sleep is too fragile for
// CI — see story Task 10 note).
public sealed class PreviewServiceTests
{
    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    // ── allowlist stubs (no Moq in the test project) ──────────────────────────────────
    private sealed class AllowAllStub : IDatasetAllowlist
    {
        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TableColumnsDto?> GetTableColumnsAsync(string tableName, CancellationToken ct) => throw new NotImplementedException();
        public bool IsAllowed(string tableName) => true;
    }

    private sealed class DenyAllStub : IDatasetAllowlist
    {
        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TableColumnsDto?> GetTableColumnsAsync(string tableName, CancellationToken ct) => throw new NotImplementedException();
        public bool IsAllowed(string tableName) => false;
    }

    // Asserts the connection factory is never reached on the early-return branches.
    private sealed class NeverCalledFactory : IPreviewConnectionFactory
    {
        public Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Connection factory must not be called on this branch.");
    }

    // Faults with the PostgreSQL statement-timeout SqlState (57014) so the catch-filter
    // in PreviewService classifies the outcome as Timeout.
    private sealed class TimeoutFactory : IPreviewConnectionFactory
    {
        public Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default) =>
            Task.FromException<NpgsqlConnection>(new PostgresException(
                "canceling statement due to statement timeout", "ERROR", "ERROR", "57014"));
    }

    [Fact]
    public async Task ExecuteAsync_CustomQuery_EmptyQuery_ReturnsSqlError()
    {
        var service = new PreviewService(new NeverCalledFactory(), new AllowAllStub(), EmptyConfig());

        var result = await service.ExecuteAsync(new PreviewRequest(IsCustomQuery: true, Query: "", BuilderState: null));

        Assert.Equal(PreviewOutcome.SqlError, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_CustomQuery_NonSelectQuery_ReturnsSqlError()
    {
        var service = new PreviewService(new NeverCalledFactory(), new AllowAllStub(), EmptyConfig());

        var result = await service.ExecuteAsync(
            new PreviewRequest(IsCustomQuery: true, Query: "DELETE FROM foo", BuilderState: null));

        Assert.Equal(PreviewOutcome.SqlError, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_BuilderMode_NullBuilderState_ReturnsBuilderStateInvalid()
    {
        var service = new PreviewService(new NeverCalledFactory(), new AllowAllStub(), EmptyConfig());

        var result = await service.ExecuteAsync(
            new PreviewRequest(IsCustomQuery: false, Query: null, BuilderState: null));

        Assert.Equal(PreviewOutcome.BuilderStateInvalid, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_BuilderMode_GeneratorErrors_ReturnsBuilderStateInvalid()
    {
        // A well-formed builder_state whose table is rejected by the (deny-all) allowlist —
        // the generator returns errors, so the service short-circuits to BuilderStateInvalid
        // before touching the connection factory.
        var service = new PreviewService(new NeverCalledFactory(), new DenyAllStub(), EmptyConfig());

        var result = await service.ExecuteAsync(new PreviewRequest(
            IsCustomQuery: false, Query: null, BuilderState: BuilderStateJson("denied_table", "id")));

        Assert.Equal(PreviewOutcome.BuilderStateInvalid, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_SqlState57014_ReturnsTimeout()
    {
        var service = new PreviewService(new TimeoutFactory(), new AllowAllStub(), EmptyConfig());

        var result = await service.ExecuteAsync(
            new PreviewRequest(IsCustomQuery: true, Query: "SELECT 1", BuilderState: null));

        Assert.Equal(PreviewOutcome.Timeout, result.Outcome);
    }

    // Minimal builder_state JSON: one node with one checked column, no joins/filters.
    private static string BuilderStateJson(
        string table, string column, string side = "left", bool isChecked = true)
    {
        var state = new
        {
            nodes = new[]
            {
                new
                {
                    id = "n1",
                    type = "tableNode",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        tableName = table,
                        side,
                        columns = new[]
                        {
                            new
                            {
                                columnName = column,
                                pgType = "integer",
                                @checked = isChecked,
                                aggregate = "none",
                                alias = "",
                            },
                        },
                    },
                },
            },
            edges = Array.Empty<object>(),
            filters = new { id = "root", kind = "group", combinator = "AND", items = Array.Empty<object>() },
            orderBy = Array.Empty<object>(),
            caseColumns = Array.Empty<object>(),
            calculatedColumns = Array.Empty<object>(),
        };
        return JsonSerializer.Serialize(state);
    }
}
