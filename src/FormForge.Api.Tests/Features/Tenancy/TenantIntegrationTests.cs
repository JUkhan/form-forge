using System.Diagnostics.CodeAnalysis;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FormForge.Api.Tests.Features.Tenancy;

// Story 12.1 — the tenants table has no API surface yet (that's Stories 12.2/12.5),
// so these tests exercise the EF Core mapping and DB constraints directly rather than
// through HTTP, following the same WebApplicationFactory + PostgresFixture shape as
// every other integration test in this project.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class TenantIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public TenantIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE tenants RESTART IDENTITY CASCADE;");
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddTenant_ValidRow_PersistsAndRoundTrips()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        var tenant = new Tenant { Name = "Acme Corp", SchemaName = "tenant_acme" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var reloaded = await verifyDb.Tenants.SingleAsync(t => t.Id == tenant.Id);

        Assert.Equal("Acme Corp", reloaded.Name);
        Assert.Equal("tenant_acme", reloaded.SchemaName);
        // Not explicitly set on the entity — asserts the "Provisioning" default (Decision 7.1/FR-74 AC-2).
        Assert.Equal("Provisioning", reloaded.Status);
        Assert.Null(reloaded.CreatedBy);
        Assert.True(reloaded.CreatedAt > DateTimeOffset.MinValue);
    }

    [Theory]
    [InlineData("Provisioning", "tenant_provisioning")]
    [InlineData("Active", "tenant_active")]
    [InlineData("Suspended", "tenant_suspended")]
    public async Task AddTenant_StatusCheckConstraint_AcceptsValidValues(string status, string schemaName)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.Tenants.Add(new Tenant { Name = "Acme Corp", SchemaName = schemaName, Status = status });

        await db.SaveChangesAsync(); // does not throw
    }

    [Fact]
    public async Task AddTenant_DuplicateSchemaName_ThrowsOnUniqueConstraint()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.Tenants.Add(new Tenant { Name = "Acme Corp", SchemaName = "tenant_acme" });
        await db.SaveChangesAsync();

        using var secondScope = _factory.Services.CreateScope();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        secondDb.Tenants.Add(new Tenant { Name = "Acme Corp (duplicate)", SchemaName = "tenant_acme" });

        // AC-1 (FR-74 / Decision 7.1): schema_name collisions are rejected. This story
        // enforces it as a DB-level backstop (uq_tenants_schema_name); a friendly
        // application-level error is Story 12.2's responsibility.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => secondDb.SaveChangesAsync());
        var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
        Assert.Equal("uq_tenants_schema_name", pgEx.ConstraintName);
    }

    [Fact]
    public async Task AddTenant_InvalidStatus_ThrowsOnCheckConstraint()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.Tenants.Add(new Tenant { Name = "Acme Corp", SchemaName = "tenant_acme", Status = "Bogus" });

        // ck_tenants_status restricts the column to Provisioning/Active/Suspended.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, pgEx.SqlState);
        Assert.Equal("ck_tenants_status", pgEx.ConstraintName);
    }

    [Fact]
    public async Task AddTenant_SchemaNameTooLong_ThrowsOnLengthLimit()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        // SafeIdentifier's rule (Decision 7.5) caps identifiers at 63 characters; the
        // column's character varying(63) is the DB-level backstop for that same bound.
        db.Tenants.Add(new Tenant { Name = "Acme Corp", SchemaName = new string('a', 64) });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
    }

    [Fact]
    public async Task AddTenant_MissingSchemaName_ThrowsOnNotNullConstraint()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        db.Tenants.Add(new Tenant { Name = "Acme Corp", SchemaName = null! });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.NotNullViolation, pgEx.SqlState);
    }
}
