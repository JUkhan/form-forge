using FormForge.Api.Features.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FormForge.Api.Tests.Features.Auth;

// Story 2.10 — unit coverage for the best-effort email transport. Both paths
// must return false WITHOUT throwing so user creation is never blocked (AC-3).
public sealed class EmailServiceTests
{
    [Fact]
    public async Task TrySendWelcomeEmail_WhenSmtpHostNotConfigured_ReturnsFalse()
    {
        var sut = new MailKitEmailService(
            Options.Create(new SmtpOptions { Host = string.Empty }),
            NullLogger<MailKitEmailService>.Instance);

        var sent = await sut.TrySendWelcomeEmailAsync(
            "newuser@example.com",
            "TempPass123!",
            "http://localhost:5173",
            correlationId: "test-correlation-id",
            CancellationToken.None);

        Assert.False(sent);
    }

    [Fact]
    public async Task TrySendWelcomeEmail_WhenSmtpServerUnreachable_ReturnsFalse()
    {
        // 127.0.0.1:1 is closed on loopback → ConnectAsync fails fast with a
        // connection-refused; the service must swallow it and report failure.
        var sut = new MailKitEmailService(
            Options.Create(new SmtpOptions { Host = "127.0.0.1", Port = 1 }),
            NullLogger<MailKitEmailService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sent = await sut.TrySendWelcomeEmailAsync(
            "newuser@example.com",
            "TempPass123!",
            "http://localhost:5173",
            correlationId: "test-correlation-id",
            cts.Token);

        Assert.False(sent);
    }

    // Story 2.11 — the password-reset send shares the welcome-email contract: both
    // failure paths must return false WITHOUT throwing so the fire-and-forget
    // dispatch never crashes a background task.
    [Fact]
    public async Task TrySendPasswordResetEmail_WhenSmtpHostNotConfigured_ReturnsFalse()
    {
        var sut = new MailKitEmailService(
            Options.Create(new SmtpOptions { Host = string.Empty }),
            NullLogger<MailKitEmailService>.Instance);

        var sent = await sut.TrySendPasswordResetEmailAsync(
            "user@example.com",
            "http://localhost:5173/reset-password?token=abc",
            correlationId: "test-correlation-id",
            CancellationToken.None);

        Assert.False(sent);
    }

    [Fact]
    public async Task TrySendPasswordResetEmail_WhenSmtpServerUnreachable_ReturnsFalse()
    {
        // 127.0.0.1:1 is closed on loopback → ConnectAsync fails fast with a
        // connection-refused; the service must swallow it and report failure.
        var sut = new MailKitEmailService(
            Options.Create(new SmtpOptions { Host = "127.0.0.1", Port = 1 }),
            NullLogger<MailKitEmailService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sent = await sut.TrySendPasswordResetEmailAsync(
            "user@example.com",
            "http://localhost:5173/reset-password?token=abc",
            correlationId: "test-correlation-id",
            cts.Token);

        Assert.False(sent);
    }
}
