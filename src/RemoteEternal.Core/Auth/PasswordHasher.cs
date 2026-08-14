using System.Security.Cryptography;
using RemoteEternal.Core.Protocol;

namespace RemoteEternal.Core.Auth;

public static class PasswordHasher
{
    public const int Iterations = 120_000;

    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(16);

    public static byte[] Compute(byte[] salt, string password) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static string ComputeBase64(string saltBase64, string password) =>
        Convert.ToBase64String(Compute(Convert.FromBase64String(saltBase64), password));
}
