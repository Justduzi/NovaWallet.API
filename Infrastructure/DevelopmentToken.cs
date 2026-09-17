using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace NovaWallet.API.Infrastructure;

public static class DevelopmentToken
{
    public static void MapDevelopmentToken(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        app.MapPost("/dev/token", (JwtOptions options, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            var expires = now.AddHours(1);
            var token = new JwtSecurityToken(options.Issuer, options.Audience,
                [new Claim("sub", "local-evaluator")], notBefore: now.UtcDateTime, expires: expires.UtcDateTime,
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    SecurityAlgorithms.HmacSha256));
            return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), tokenType = "Bearer", expiresAtUtc = expires });
        }).AllowAnonymous().WithTags("Development only - non-production token issuer");
    }
}
