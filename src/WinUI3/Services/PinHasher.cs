using System.Security.Cryptography;

namespace WindowsAppProtector.Services;

public static class PinHasher
{
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int Iterations = 120_000;

    public static (string Salt, string Hash) CreateHash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = DeriveHash(pin, salt);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string pin, string saltText, string expectedHashText)
    {
        if (string.IsNullOrWhiteSpace(pin) ||
            string.IsNullOrWhiteSpace(saltText) ||
            string.IsNullOrWhiteSpace(expectedHashText))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(saltText);
            var expectedHash = Convert.FromBase64String(expectedHashText);
            var actualHash = DeriveHash(pin, salt);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DeriveHash(string pin, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(HashLength);
    }
}
