using FormForge.Api.Features.Auth;

namespace FormForge.Api.Tests.Features.Auth;

public sealed class PasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_ReturnsNonEmptyBcryptString()
    {
        var hash = _hasher.Hash("Password1!");

        Assert.False(string.IsNullOrEmpty(hash));
        // BCrypt hashes start with $2a$, $2b$, or $2y$ followed by the work factor.
        Assert.StartsWith("$2", hash, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        const string password = "Password1!";
        var hash = _hasher.Hash(password);

        Assert.True(_hasher.Verify(password, hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = _hasher.Hash("Password1!");

        Assert.False(_hasher.Verify("WrongPassword!", hash));
    }

    [Fact]
    public void Hash_SamePassword_ProducesDifferentHashes()
    {
        const string password = "Password1!";

        var hashA = _hasher.Hash(password);
        var hashB = _hasher.Hash(password);

        Assert.NotEqual(hashA, hashB);
        Assert.True(_hasher.Verify(password, hashA));
        Assert.True(_hasher.Verify(password, hashB));
    }

    [Fact]
    public void Verify_MalformedHash_ReturnsFalseWithoutThrowing()
    {
        // A non-parseable BCrypt-shaped string must surface as "wrong password" rather
        // than a thrown SaltParseException — otherwise the constant-time dummy-hash
        // guard becomes a status-code oracle for unknown emails.
        Assert.False(_hasher.Verify("Password1!", "$2a$12$not-actually-a-valid-hash"));
        Assert.False(_hasher.Verify("Password1!", "definitely-not-bcrypt"));
        Assert.False(_hasher.Verify("Password1!", string.Empty));
    }
}
