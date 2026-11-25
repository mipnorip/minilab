using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace EJL.Util;

/// <summary>Пользователь из JWT (или системный актор для внутренних вызовов).</summary>
public sealed record CurrentUser(int Id, string? Login, string? Name, string Role, bool Pwp)
{
    public static CurrentUser System { get; } = new(0, "system", "system", "system", true);
}

/// <summary>Выпуск и проверка JWT (HS256), claims: sub, login, name, role, pwp, exp.</summary>
public sealed class JwtService(byte[] secret)
{
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly TokenValidationParameters _params = new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        IssuerSigningKey = new SymmetricSecurityKey(secret),
    };

    public string Issue(int id, string login, string name, string role, bool pwp, TimeSpan ttl)
    {
        var jwt = new JwtSecurityToken(
            claims:
            [
                new Claim("sub", id.ToString()),
                new Claim("login", login),
                new Claim("name", name),
                new Claim("role", role),
                new Claim("pwp", pwp ? "true" : "false"),
            ],
            expires: DateTime.UtcNow.Add(ttl),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(secret), SecurityAlgorithms.HmacSha256));
        return _handler.WriteToken(jwt);
    }

    /// <summary>null, если токен неверный или истёк.</summary>
    public CurrentUser? Verify(string token)
    {
        try
        {
            _handler.ValidateToken(token, _params, out var validated);
            if (validated is not JwtSecurityToken jt) return null;
            string? Get(string type) =>
                jt.Claims.FirstOrDefault(c => c.Type == type)?.Value;
            if (!int.TryParse(Get("sub"), out var id)) return null;
            return new CurrentUser(id, Get("login"), Get("name"), Get("role") ?? "", Get("pwp") == "true");
        }
        catch
        {
            return null;
        }
    }

    public static byte[] LoadOrCreateSecret(string path)
    {
        if (File.Exists(path))
        {
            var hex = File.ReadAllText(path).Trim();
            if (hex.Length == 128) return Convert.FromHexString(hex);
        }
        var bytes = RandomNumberGenerator.GetBytes(64);
        File.WriteAllText(path, Convert.ToHexString(bytes).ToLowerInvariant());
        return bytes;
    }
}
