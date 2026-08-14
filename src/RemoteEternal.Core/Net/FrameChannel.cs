using System.Buffers.Binary;
using System.IO;

namespace RemoteEternal.Core.Net;

public static class FrameChannel
{
    public const int MaxFrameSize = 64 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        await ReadExactlyAsync(stream, header, 4, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len < 0 || len > MaxFrameSize)
            throw new IOException($"Invalid frame length: {len}");
        byte[] payload = new byte[len];
        await ReadExactlyAsync(stream, payload, len, ct).ConfigureAwait(false);
        return payload;
    }

    public static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
