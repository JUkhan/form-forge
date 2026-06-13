using System.Diagnostics.CodeAnalysis;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Provisioning;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Provisioning;

// Story 5.5 — CycleDetector unit-style tests against a real PostgresFixture so the
// DFS over EF query results exercises the actual Npgsql LINQ pipeline (ordering,
// Status filter, AsNoTracking semantics). Seeds component_schemas +
// component_schema_versions directly via FormForgeDbContext for speed; resolves
// the CycleDetector from the test host scope so DI registration is exercised too.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class CycleDetectorTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public CycleDetectorTests(PostgresFixture postgres) => _postgres = postgres;

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

        // Only the schema tables need clearing — CycleDetector reads no other tables.
        // RESTART IDENTITY CASCADE keeps the FK chain (versions → schemas) consistent.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE component_schema_versions, component_schemas RESTART IDENTITY CASCADE;");
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task HasCycle_NoRepeater_ReturnsFalse()
    {
        await SeedDesignerAsync("no_rep_designer", version: 1, status: "Published",
            rootElement: EmptyStackJson());

        Assert.False(await DetectAsync("no_rep_designer", 1));
    }

    [Fact]
    public async Task HasCycle_SingleLevelNoChildPublished_ReturnsFalse()
    {
        // Parent points to a child whose only version is Draft. CycleDetector must
        // skip the unpublished child rather than throw (AC-4 / Task 1 step 5).
        await SeedDesignerAsync("single_unpub_parent", version: 1, status: "Published",
            rootElement: RepeaterJson("single_unpub_child"));
        await SeedDesignerAsync("single_unpub_child", version: 1, status: "Draft",
            rootElement: EmptyStackJson());

        Assert.False(await DetectAsync("single_unpub_parent", 1));
    }

    [Fact]
    public async Task HasCycle_DirectSelfReference_ReturnsFalse()
    {
        // designer A v1 has a Repeater whose rowDesignerId points back to "self_ref".
        // A direct self-edge is an intentional adjacency-list TREE, not a cycle: the
        // provisioner emits one table with a parent_self_ref_id self-FK and does not
        // recurse. The detector must allow it.
        await SeedDesignerAsync("self_ref", version: 1, status: "Published",
            rootElement: RepeaterJson("self_ref"));

        Assert.False(await DetectAsync("self_ref", 1));
    }

    [Fact]
    public async Task HasCycle_SelfReferenceNestedUnderParent_ReturnsFalse()
    {
        // A → B, and B → B (B is a self-referencing tree). The self-edge at the
        // non-root node B must also be treated as a tree, not a cycle.
        await SeedDesignerAsync("nest_a", version: 1, status: "Published",
            rootElement: RepeaterJson("nest_b"));
        await SeedDesignerAsync("nest_b", version: 1, status: "Published",
            rootElement: RepeaterJson("nest_b"));

        Assert.False(await DetectAsync("nest_a", 1));
    }

    [Fact]
    public async Task HasCycle_IndirectCycle_ReturnsTrue()
    {
        // A → B → A. Both must have Published versions for the DFS to traverse.
        await SeedDesignerAsync("ind_a", version: 1, status: "Published",
            rootElement: RepeaterJson("ind_b"));
        await SeedDesignerAsync("ind_b", version: 1, status: "Published",
            rootElement: RepeaterJson("ind_a"));

        Assert.True(await DetectAsync("ind_a", 1));
    }

    [Fact]
    public async Task HasCycle_LinearChain_ReturnsFalse()
    {
        // A → B → C, no back-edge. Verifies visited-set bookkeeping doesn't produce
        // false positives when the same node is approached via different paths.
        await SeedDesignerAsync("lin_a", version: 1, status: "Published",
            rootElement: RepeaterJson("lin_b"));
        await SeedDesignerAsync("lin_b", version: 1, status: "Published",
            rootElement: RepeaterJson("lin_c"));
        await SeedDesignerAsync("lin_c", version: 1, status: "Published",
            rootElement: EmptyStackJson());

        Assert.False(await DetectAsync("lin_a", 1));
    }

    // ---------- Helpers ----------

    private async Task<bool> DetectAsync(string rootDesignerId, int rootVersion)
    {
        using var scope = _factory!.Services.CreateScope();
        var detector = scope.ServiceProvider.GetRequiredService<CycleDetector>();
        return await detector.HasCycleAsync(rootDesignerId, rootVersion, CancellationToken.None);
    }

    private async Task SeedDesignerAsync(
        string designerId,
        int version,
        string status,
        string rootElement)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        var schemaExists = await db.ComponentSchemas
            .AsNoTracking()
            .AnyAsync(s => s.DesignerId == designerId);
        if (!schemaExists)
        {
            db.ComponentSchemas.Add(new ComponentSchema
            {
                DesignerId = designerId,
                DisplayName = designerId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        db.ComponentSchemaVersions.Add(new ComponentSchemaVersion
        {
            DesignerId = designerId,
            Version = version,
            Status = status,
            RootElement = rootElement,
            CreatedAt = DateTimeOffset.UtcNow,
            PublishedAt = status == "Published" ? DateTimeOffset.UtcNow : null,
        });
        await db.SaveChangesAsync();
    }

    private static string EmptyStackJson() =>
        """{"id":"root","type":"Stack","properties":{},"children":[]}""";

    private static string RepeaterJson(string rowDesignerId) =>
        $$"""
        {
          "id":"root","type":"Stack","properties":{},"children":[
            {"id":"rep","type":"Repeater","properties":{"rowDesignerId":"{{rowDesignerId}}"},"children":[]}
          ]
        }
        """;
}
