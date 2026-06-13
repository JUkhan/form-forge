using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Auth;
using Microsoft.Extensions.Options;

namespace FormForge.Api.Tests.Features.Auth;

public sealed class JwtTokenServiceTests
{
    private static readonly JwtOptions DefaultOptions = new()
    {
        SigningKey = "test-signing-key-minimum-32-characters!!",
        Issuer = "FormForge",
        Audience = "FormForge",
        AccessTokenTtlMinutes = 15,
    };

    private static JwtTokenService CreateService(JwtOptions? options = null) =>
        new(Options.Create(options ?? DefaultOptions));

    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        Email = "test@example.com",
        DisplayName = "Test User",
        PasswordHash = "ignored-in-this-test",
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void CreateAccessToken_ReturnsThreeDotSeparatedJwt()
    {
        var service = CreateService();
        var token = service.CreateAccessToken(CreateUser(), []);

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.False(string.IsNullOrEmpty(p)));
    }

    [Fact]
    public void CreateAccessToken_ContainsExpectedClaims()
    {
        var user = CreateUser();
        var service = CreateService();
        var token = service.CreateAccessToken(user, []);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(user.Id.ToString(), jwt.Claims.First(c => c.Type == "userId").Value);
        Assert.Equal(user.Email, jwt.Claims.First(c => c.Type == "email").Value);
        Assert.Contains(jwt.Claims, c => c.Type == "iat");
        Assert.Contains(jwt.Claims, c => c.Type == "exp");
    }

    [Fact]
    public void CreateAccessToken_ExpiresIn15Minutes()
    {
        var service = CreateService();
        var before = DateTime.UtcNow;
        var token = service.CreateAccessToken(CreateUser(), []);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var ttl = jwt.ValidTo - before;
        // Allow ±5 seconds clock slack for the test.
        Assert.InRange(ttl.TotalSeconds, 15 * 60 - 5, 15 * 60 + 5);
    }

    [Fact]
    public void CreateAccessToken_WithRoles_IncludesRolesClaims()
    {
        var service = CreateService();
        var token = service.CreateAccessToken(CreateUser(), ["admin", "editor"]);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var roles = jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        Assert.Equal(2, roles.Count);
        Assert.Contains("admin", roles, StringComparer.Ordinal);
        Assert.Contains("editor", roles, StringComparer.Ordinal);
    }

    [Fact]
    public void CreateAccessToken_WithEmptyRoles_HasNoRolesClaims()
    {
        var service = CreateService();
        var token = service.CreateAccessToken(CreateUser(), []);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.DoesNotContain(jwt.Claims, c => c.Type == "roles");
    }

    [Fact]
    public void CreateAccessToken_MissingSigningKey_Throws()
    {
        var bad = new JwtOptions { SigningKey = string.Empty };
        var service = CreateService(bad);

        Assert.Throws<InvalidOperationException>(() => service.CreateAccessToken(CreateUser(), []));
    }

    [Fact]
    public void CreateAccessToken_IatClaim_IsEmittedAsNumericDate()
    {
        // RFC 7519 §2: `iat` is a NumericDate (JSON number). With ClaimValueTypes.Integer64,
        // System.IdentityModel.Tokens.Jwt serializes as an unquoted JSON number. Re-parsing
        // via ReadJwtToken surfaces the value as a string in Claim.Value, but the ValueType
        // remains Integer64 — that's what we lock down here.
        var service = CreateService();
        var token = service.CreateAccessToken(CreateUser(), []);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var iat = Assert.Single(jwt.Claims, c => c.Type == "iat");
        Assert.Equal(ClaimValueTypes.Integer64, iat.ValueType);
        Assert.True(long.TryParse(iat.Value, out _), "iat value must be a parseable integer");
    }
}
