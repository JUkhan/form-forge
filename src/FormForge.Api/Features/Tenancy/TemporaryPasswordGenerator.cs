using System.Security.Cryptography;

namespace FormForge.Api.Features.Tenancy;

// Story 12.5 — generates the tenant's first admin user's temporary password
// server-side (no client-supplied password for this flow — see this story's Intent).
// No existing password-generation utility exists in this codebase to reuse
// (MfaService.GenerateBackupCode is uppercase-alnum only and sized for a backup code,
// not password strength) — this is new, deliberately narrow.
internal interface ITemporaryPasswordGenerator
{
    string Generate();
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class TemporaryPasswordGenerator : ITemporaryPasswordGenerator
{
    // 24 chars from a mixed-case + digit + symbol alphabet comfortably clears
    // ChangePasswordRequestValidator's 8-char floor and stays well under BCrypt's
    // 72-byte UTF-8 ceiling. Ambiguous glyphs (0/O, 1/I/l) are excluded since an
    // operator may need to read this aloud or retype it when relaying it out-of-band.
    private const int Length = 24;
    private const string Alphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*-_";

    public string Generate()
    {
        // Each character independently drawn from a CSPRNG via RandomNumberGenerator
        // .GetInt32 — same per-char draw pattern as MfaService.GenerateBackupCode.
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
