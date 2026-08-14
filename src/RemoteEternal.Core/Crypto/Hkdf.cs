using System.Security.Cryptography;

namespace RemoteEternal.Core.Crypto;

public static class Hkdf
{
    public static byte[] DeriveKey(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        byte[] prk;
        using (var h = new HMACSHA256(salt.ToArray()))
            prk = h.ComputeHash(ikm.ToArray());

        var output = new byte[length];
        var prev = Array.Empty<byte>();
        int offset = 0;
        for (byte counter = 1; offset < length; counter++)
        {
            var input = new byte[prev.Length + info.Length + 1];
            prev.CopyTo(input, 0);
            info.CopyTo(input.AsSpan(prev.Length));
            input[^1] = counter;
            using var h = new HMACSHA256(prk);
            prev = h.ComputeHash(input);
            int copy = Math.Min(prev.Length, length - offset);
            Array.Copy(prev, 0, output, offset, copy);
            offset += copy;
        }
        return output;
    }
}
