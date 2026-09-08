using System.Diagnostics.CodeAnalysis;
using FormForge.Api.Infrastructure.Persistence;
using FormForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormForge.Api.Tests.Features.Auth;

// Story 12.4 — covers Program.cs's startup bootstrap: seeds exactly one
// public.platform_admins row on first boot (never into any `users` table), using the
// same hardcoded admin@formforge.local / Admin1234! constants as before, and skips
// seeding entirely once a row already exists (idempotent across restarts). Each
// WebApplicationFactory build re-executes Program.cs's top-level startup code
// (migrate + bootstrap check), so building a fresh factory against the same Postgres
// container simulates a process restart without needing to actually restart anything.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class PlatformAdminBootstrapTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;

    public PlatformAdminBootstrapTests(PostgresFixture postgres) => _postgres = postgres;

    // Migrate the schema once and truncate platform_admins so every test starts from
    // a genuinely empty table — including undoing whatever this very call's own
    // WebApplicationFactory startup bootstrap just seeded.
    public async Task InitializeAsync()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE platform_admins RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:formforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });

    [Fact]
    public async Task FirstBoot_EmptyPlatformAdmins_SeedsOneRow()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        var admins = await db.PlatformAdmins.AsNoTracking().ToListAsync();

        var admin = Assert.Single(admins);
        Assert.Equal("admin@formforge.local", admin.UserEmail);
        Assert.True(BCrypt.Net.BCrypt.Verify("Admin1234!", admin.PasswordHash));
    }

    [Fact]
    public async Task FirstBoot_NeverSeedsIntoUsersTable()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        var usersCount = await db.Users.AsNoTracking().CountAsync();

        Assert.Equal(0, usersCount);
    }

    [Fact]
    public async Task SecondBoot_PlatformAdminsNonEmpty_BootstrapSkipped_StaysIdempotent()
    {
        Guid firstId;
        await using (var firstFactory = CreateFactory())
        {
            using var scope = firstFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
            firstId = (await db.PlatformAdmins.AsNoTracking().SingleAsync()).Id;
        }

        // A brand-new factory against the SAME Postgres container simulates a restart:
        // Program.cs's `if (!db.PlatformAdmins.Any())` check now sees the row the first
        // factory seeded and must skip re-seeding.
        await using var secondFactory = CreateFactory();
        using var secondScope = secondFactory.Services.CreateScope();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<FormForgeDbContext>();

        var admins = await secondDb.PlatformAdmins.AsNoTracking().ToListAsync();
        var admin = Assert.Single(admins);
        Assert.Equal(firstId, admin.Id);
    }
}
