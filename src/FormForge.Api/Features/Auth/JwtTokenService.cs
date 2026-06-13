using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FormForge.Api.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FormForge.Api.Features.Auth;

internal interface IJwtTokenService
{
    string CreateAccessToken(User user, IReadOnlyList<string> roleNames);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class JwtTokenService(IOptions<JwtOptions> jwtOptions) : IJwtTokenService
{
    public string CreateAccessToken(User user, IReadOnlyList<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roleNames);

        var options = jwtOptions.Value;

        if (string.IsNullOrWhiteSpace(options.SigningKey))
            throw new InvalidOperationException("Jwt:SigningKey is required.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;

        // `iat` is NOT auto-emitted by JwtSecurityToken (it emits `nbf` from notBefore
        // and `exp` from expires only). Add it explicitly with Integer64 so the JWT
        // payload serializer writes it as a JSON number per RFC 7519 §2 (NumericDate).
        var issuedAtUnix = new DateTimeOffset(now).ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var claims = new List<Claim>
        {
            new("userId", user.Id.ToString()),
            new("email", user.Email),
            new("iat", issuedAtUnix, ClaimValueTypes.Integer64),
        };

        foreach (var role in roleNames)
        {
            claims.Add(new Claim("roles", role));
        }

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(options.AccessTokenTtlMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
