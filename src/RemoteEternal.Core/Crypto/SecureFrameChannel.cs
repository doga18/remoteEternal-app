using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using RemoteEternal.Core.Net;

namespace RemoteEternal.Core.Crypto;

/// <summary>
/// Identifies the endpoint role used to map directional session keys.
/// </summary>
public enum SessionRole
{
    /// <summary>The endpoint that sends with the HKDF <c>write</c> key.</summary>
    Host,

    /// <summary>The endpoint that sends with the HKDF <c>read</c> key.</summary>
    Client
}

public sealed class SecureFrameChannel
{
    /// <summary>
    /// The salt used by the version 1 session key derivation.
    /// </summary>
    public const string SessionSaltV1 = "remoteEternal-session-v1";

    public const byte TypeControl = 0;
    public const byte TypeMedia = 1;
    public const byte TypeInput = 2;
    public const byte TypeAudio = 3;

    public static ReadOnlySpan<byte> LabelWrite => "write"u8;
    public static ReadOnlySpan<byte> LabelRead => "read"u8;

    private static readonly byte[] Salt = System.Text.Encoding.UTF8.GetBytes(SessionSaltV1);

    private readonly Stream _stream;
    private readonly byte[] _keyWrite;
    private readonly byte[] _keyRead;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _sendCounter;
    private long _recvCounter;

    private SecureFrameChannel(Stream stream, byte[] keyWrite, byte[] keyRead)
    {
        _stream = stream;
        _keyWrite = keyWrite;
        _keyRead = keyRead;
    }

    /// <summary>
    /// Creates a channel using the legacy single-key session derivation.
    /// </summary>
    /// <remarks>
    /// This legacy factory derives the key with <see cref="SessionSaltV1"/>.
    /// For directional sessions, use <see cref="CreateDirectional(Stream, ReadOnlySpan{byte}, ReadOnlySpan{byte}, ReadOnlySpan{byte}, SessionRole)"/>
    /// and pass <c>Encoding.UTF8.GetBytes(SecureFrameChannel.SessionSaltV1)</c>
    /// from both the host and client.
    /// </remarks>
    public static SecureFrameChannel FromSecret(Stream stream, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> aadInfo)
    {
        byte[] key = Hkdf.DeriveKey(secret, Salt, aadInfo, 32);
        return new SecureFrameChannel(stream, key, key);
    }

    /// <summary>
    /// Creates a directional channel using the legacy host mapping.
    /// </summary>
    /// <remarks>
    /// This overload is retained for source and binary compatibility and delegates
    /// to <see cref="CreateDirectional(Stream, ReadOnlySpan{byte}, ReadOnlySpan{byte}, ReadOnlySpan{byte}, SessionRole)"/>
    /// with <see cref="SessionRole.Host"/>. New host/client code should provide
    /// an explicit role and pass <c>Encoding.UTF8.GetBytes(SecureFrameChannel.SessionSaltV1)</c>
    /// as the same salt on both endpoints.
    /// </remarks>
    public static SecureFrameChannel CreateDirectional(
        Stream stream,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info)
        => CreateDirectional(stream, secret, salt, info, SessionRole.Host);

    /// <summary>
    /// Creates a directional channel and maps the derived keys for the endpoint role.
    /// </summary>
    /// <remarks>
    /// HKDF derives <c>keyWrite</c> from <c>info + "write"</c> and <c>keyRead</c>
    /// from <c>info + "read"</c>. For <see cref="SessionRole.Host"/>, the channel
    /// encrypts with <c>keyWrite</c> and decrypts with <c>keyRead</c>. For
    /// <see cref="SessionRole.Client"/>, the assignments are inverted: it encrypts
    /// with <c>keyRead</c> and decrypts with <c>keyWrite</c>. Host and client must
    /// pass the same salt; for the v1 session use
    /// <c>Encoding.UTF8.GetBytes(SecureFrameChannel.SessionSaltV1)</c>. The wire format and
    /// cryptographic parameters are unchanged.
    /// </remarks>
    public static SecureFrameChannel CreateDirectional(
        Stream stream,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        SessionRole role)
    {
        byte[] writeInfo = AppendLabel(info, LabelWrite);
        byte[] readInfo = AppendLabel(info, LabelRead);
        byte[] keyWrite = Hkdf.DeriveKey(secret, salt, writeInfo, 32);
        byte[] keyRead = Hkdf.DeriveKey(secret, salt, readInfo, 32);

        return role switch
        {
            SessionRole.Host => new SecureFrameChannel(stream, keyWrite, keyRead),
            SessionRole.Client => new SecureFrameChannel(stream, keyRead, keyWrite),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown session role.")
        };
    }

    private static byte[] AppendLabel(ReadOnlySpan<byte> info, ReadOnlySpan<byte> label)
    {
        byte[] result = new byte[info.Length + label.Length];
        info.CopyTo(result);
        label.CopyTo(result.AsSpan(info.Length));
        return result;
    }

    public async Task SendAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        const int overhead = 1 + 12 + 16;
        if (payload.Length > FrameChannel.MaxFrameSize - overhead)
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload exceeds the secure frame size limit.");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long counter = Interlocked.Increment(ref _sendCounter);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);

            byte[] aad = new byte[9];
            aad[0] = type;
            BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(1), counter);

            byte[] cipher = new byte[payload.Length];
            byte[] tag = new byte[16];
            using (var aes = new AesGcm(_keyWrite, 16))
            {
                aes.Encrypt(nonce, payload.Span, cipher, tag, aad);
            }

            int total = overhead + cipher.Length;
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, total);

            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            await _stream.WriteAsync(new[] { type }, ct).ConfigureAwait(false);
            await _stream.WriteAsync(nonce, ct).ConfigureAwait(false);
            await _stream.WriteAsync(cipher, ct).ConfigureAwait(false);
            await _stream.WriteAsync(tag, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<(byte Type, byte[] Payload)> ReceiveAsync(CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        await FrameChannel.ReadExactlyAsync(_stream, header, 4, ct).ConfigureAwait(false);
        int total = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (total < 1 + 12 + 16 || total > FrameChannel.MaxFrameSize)
            throw new IOException($"Invalid secure frame length: {total}");

        byte[] body = new byte[total];
        await FrameChannel.ReadExactlyAsync(_stream, body, total, ct).ConfigureAwait(false);

        byte type = body[0];
        byte[] nonce = body.AsSpan(1, 12).ToArray();
        byte[] cipher = body.AsSpan(13, total - 1 - 12 - 16).ToArray();
        byte[] tag = body.AsSpan(13 + cipher.Length, 16).ToArray();

        long counter = Interlocked.Increment(ref _recvCounter);
        byte[] aad = new byte[9];
        aad[0] = type;
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(1), counter);

        byte[] plain = new byte[cipher.Length];
        using (var aes = new AesGcm(_keyRead, 16))
        {
            aes.Decrypt(nonce, cipher, tag, plain, aad);
        }

        return (type, plain);
    }
}
