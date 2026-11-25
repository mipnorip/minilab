using System.Security.Cryptography;
using System.Text;

namespace EJL.Util;

/// <summary>PBKDF2-SHA256, формат хранения «salt_hex$digest_hex» — как в прежнем auth-svc.</summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var saltHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // 32 символа
        return $"{saltHex}${Compute(password, saltHex)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 2) return false;
        var candidate = Encoding.ASCII.GetBytes(Compute(password, parts[0]));
        var expected = Encoding.ASCII.GetBytes(parts[1]);
        return CryptographicOperations.FixedTimeEquals(candidate, expected);
    }

    private static string Compute(string password, string saltHex) =>
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Encoding.ASCII.GetBytes(saltHex), // как в Python: salt.encode() — байты hex-строки
            Iterations, HashAlgorithmName.SHA256, 32)).ToLowerInvariant();
}
