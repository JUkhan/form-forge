using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace FormForge.Api.Features.Auth;

// Story 2.10 (AR-53) — transactional email transport. Welcome email on user
// creation is the first consumer; password-reset / MFA stories reuse this.
internal interface IEmailService
{
    // Best-effort send. Returns true on success, false if SMTP is unconfigured
    // or delivery failed. Never throws for delivery problems — callers decide how
    // to surface a failure (e.g. a non-fatal warning in the HTTP response).
    Task<bool> TrySendWelcomeEmailAsync(
        string recipientEmail,
        string temporaryPassword,
        string loginBaseUrl,
        string correlationId,
        CancellationToken ct);

    // Story 2.11 — best-effort password-reset email. Same contract as the welcome
    // send: returns false (never throws) when SMTP is unconfigured or delivery
    // fails, so the forgot-password endpoint can fire-and-forget without affecting
    // its always-200 anti-enumeration response.
    Task<bool> TrySendPasswordResetEmailAsync(
        string recipientEmail,
        string resetUrl,
        string correlationId,
        CancellationToken ct);
}

internal sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    // Empty Host => email is disabled (the service logs a skip and returns false).
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1025;
    public string User { get; init; } = string.Empty;
    public string Pass { get; init; } = string.Empty;
    public string From { get; init; } = "noreply@formforge.local";

    // When set, used as the login-link base URL in outbound emails instead of
    // deriving it from the inbound request's Host header. Set this in production
    // to avoid relying on the Host header (e.g. "https://app.formforge.com").
    public string? BaseUrl { get; init; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class MailKitEmailService(
    IOptions<SmtpOptions> options,
    ILogger<MailKitEmailService> logger) : IEmailService
{
    private const string TemplateType = "welcome";
    private readonly SmtpOptions _smtp = options.Value;

    public async Task<bool> TrySendWelcomeEmailAsync(
        string recipientEmail,
        string temporaryPassword,
        string loginBaseUrl,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_smtp.Host))
        {
            EmailServiceLog.WelcomeEmailSkipped(logger, recipientEmail, TemplateType, correlationId);
            return false;
        }

        try
        {
            // P1: message construction is inside the try block so FormatException from
            // MailboxAddress.Parse (malformed From config or recipient) is caught below
            // and returns false rather than escaping as an unhandled exception.
            using var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(_smtp.From));
            message.To.Add(MailboxAddress.Parse(recipientEmail));
            message.Subject = "Welcome to FormForge";
            message.Body = new TextPart("plain")
            {
                Text = $"""
                    Welcome to FormForge!

                    Your account has been created. Here are your credentials:

                    Email:    {recipientEmail}
                    Password: {temporaryPassword}

                    Login at: {loginBaseUrl}/login

                    Please change your password after your first login.
                    """,
            };

            // SmtpClient is not thread-safe — a fresh instance per send (the service
            // itself is registered as a singleton, which is fine: it holds only config).
            using var client = new SmtpClient();
            await client.ConnectAsync(_smtp.Host, _smtp.Port, SecureSocketOptions.None, ct)
                .ConfigureAwait(false);

            // P2: only authenticate when both User and Pass are provided; sending an
            // empty password to AuthenticateAsync can silently succeed on some servers.
            if (!string.IsNullOrEmpty(_smtp.User) && !string.IsNullOrEmpty(_smtp.Pass))
            {
                await client.AuthenticateAsync(_smtp.User, _smtp.Pass, ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

            EmailServiceLog.WelcomeEmailDispatched(logger, recipientEmail, TemplateType, correlationId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallow every delivery error (connection refused, timeout, auth, etc.)
            // so user creation is never blocked. OperationCanceledException (the
            // caller's 3 s timeout) is rethrown for the caller to handle/log.
            EmailServiceLog.WelcomeEmailFailed(logger, ex, recipientEmail, TemplateType, correlationId);
            return false;
        }
    }

    public async Task<bool> TrySendPasswordResetEmailAsync(
        string recipientEmail,
        string resetUrl,
        string correlationId,
        CancellationToken ct)
    {
        const string templateType = "password-reset";

        if (string.IsNullOrEmpty(_smtp.Host))
        {
            EmailServiceLog.PasswordResetEmailSkipped(logger, recipientEmail, templateType, correlationId);
            return false;
        }

        try
        {
            // Message construction is inside the try so a FormException from
            // MailboxAddress.Parse (malformed From config or recipient) returns
            // false rather than escaping the fire-and-forget Task.Run.
            using var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(_smtp.From));
            message.To.Add(MailboxAddress.Parse(recipientEmail));
            message.Subject = "Reset your FormForge password";
            message.Body = new TextPart("plain")
            {
                Text = $"""
                    You requested a password reset for your FormForge account.

                    Click the link below to set a new password (valid for 1 hour):

                    {resetUrl}

                    If you did not request a password reset, you can ignore this email.
                    """,
            };

            using var client = new SmtpClient();
            await client.ConnectAsync(_smtp.Host, _smtp.Port, SecureSocketOptions.None, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_smtp.User) && !string.IsNullOrEmpty(_smtp.Pass))
            {
                await client.AuthenticateAsync(_smtp.User, _smtp.Pass, ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

            EmailServiceLog.PasswordResetEmailDispatched(logger, recipientEmail, templateType, correlationId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EmailServiceLog.PasswordResetEmailFailed(logger, ex, recipientEmail, templateType, correlationId);
            return false;
        }
    }
}

internal static partial class EmailServiceLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Welcome email skipped — SMTP not configured. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
    public static partial void WelcomeEmailSkipped(ILogger logger, string recipient, string templateType, string correlationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Welcome email dispatched. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
    public static partial void WelcomeEmailDispatched(ILogger logger, string recipient, string templateType, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Welcome email dispatch failed. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
    public static partial void WelcomeEmailFailed(ILogger logger, Exception exception, string recipient, string templateType, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Password reset email skipped — SMTP not configured. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
    public static partial void PasswordResetEmailSkipped(ILogger logger, string recipient, string templateType, string correlationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Password reset email dispatched. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
    public static partial void PasswordResetEmailDispatched(ILogger logger, string recipient, string templateType, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Password reset email dispatch failed. recipient={Recipient} templateType={TemplateType} correlationId={CorrelationId}")]
    public static partial void PasswordResetEmailFailed(ILogger logger, Exception exception, string recipient, string templateType, string correlationId);
}
