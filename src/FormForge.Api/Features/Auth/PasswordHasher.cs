namespace FormForge.Api.Features.Auth;

internal interface IPasswordHasher
{
    string Hash(string plaintext);
    bool Verify(string plaintext, string hash);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class BcryptPasswordHasher : IPasswordHasher
{
    // Decision 2.4 / AR-13: BCrypt work factor 12 (~250ms on typical hardware).
    private const int WorkFactor = 12;

    public string Hash(string plaintext) =>
        BCrypt.Net.BCrypt.HashPassword(plaintext, WorkFactor);

    public bool Verify(string plaintext, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(plaintext, hash);
        }
#pragma warning disable CA1031 // Defense-in-depth: any failure inside Verify (SaltParseException, HashInformationException, ArgumentException for empty input, etc.) must surface as "wrong password" so the constant-time dummy-hash guard remains effective and cannot become a status-code oracle.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
