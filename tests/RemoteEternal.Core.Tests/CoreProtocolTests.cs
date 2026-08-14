using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RemoteEternal.Core.Auth;
using RemoteEternal.Core.Crypto;
using RemoteEternal.Core.Net;
using RemoteEternal.Core.Protocol;
using Xunit;

namespace RemoteEternal.Core.Tests;

public sealed class CoreProtocolTests
{
    [Fact]
    public void Envelope_ParseData_RoundTripsAfterDispose()
    {
        var bytes = EnvelopeUtil.Create("sample", new RegisterHostRequest("QA-PC", "Windows 11"));
        var env = EnvelopeUtil.Parse(bytes);
        Assert.Equal("sample", env.Type);
        var item = EnvelopeUtil.Data<RegisterHostRequest>(env);
        Assert.NotNull(item);
        Assert.Equal("QA-PC", item.DeviceName);
        Assert.Contains("deviceName", Encoding.UTF8.GetString(bytes));
        var noData = EnvelopeUtil.Parse(EnvelopeUtil.Create("empty"));
        Assert.Null(noData.DataJson);
        Assert.Null(EnvelopeUtil.Data<RegisterHostRequest>(noData));
    }

    [Fact]
    public async Task FrameChannel_RoundTripsAndReadsPartialStream()
    {
        using var input = new FragmentedStream(Encoding.UTF8.GetBytes("\x03\0\0\0abc"), 1);
        Assert.Equal("abc", Encoding.UTF8.GetString(await FrameChannel.ReadFrameAsync(input)));
        using var output = new MemoryStream();
        await FrameChannel.WriteFrameAsync(output, Encoding.UTF8.GetBytes("payload"));
        Assert.Equal("payload", Encoding.UTF8.GetString(await FrameChannel.ReadFrameAsync(new MemoryStream(output.ToArray()))));
    }

    [Fact]
    public async Task FrameChannel_RejectsNegativeAndOversizedLengths()
    {
        static async Task Read(byte[] data) => await FrameChannel.ReadFrameAsync(new MemoryStream(data));
        await Assert.ThrowsAsync<IOException>(() => Read(BitConverter.GetBytes(-1)));
        await Assert.ThrowsAsync<IOException>(() => Read(BitConverter.GetBytes(FrameChannel.MaxFrameSize + 1)));
    }

    [Fact]
    public void PasswordHasher_HasExpectedDeterministicProperties()
    {
        var salt = PasswordHasher.GenerateSalt();
        Assert.Equal(16, salt.Length);
        var a = PasswordHasher.Compute(salt, "qa-only-password");
        Assert.Equal(a, PasswordHasher.Compute(salt, "qa-only-password"));
        Assert.NotEqual(a, PasswordHasher.Compute(salt, "different-password"));
        Assert.Equal(Convert.ToBase64String(a), PasswordHasher.ComputeBase64(Convert.ToBase64String(salt), "qa-only-password"));
    }

    [Fact]
    public void Hkdf_MatchesRfc5869A1()
    {
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        var expected = Convert.FromHexString("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");
        Assert.Equal(expected, Hkdf.DeriveKey(ikm, salt, info, 42));
    }

    [Fact]
    public async Task SecureFrameChannel_DirectionalRoundTripCountersAndTypes()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        var host = SecureFrameChannel.CreateDirectional(pair.Server, Secret, Salt, Info, SessionRole.Host);
        var client = SecureFrameChannel.CreateDirectional(pair.Client, Secret, Salt, Info, SessionRole.Client);
        await host.SendAsync(SecureFrameChannel.TypeControl, Encoding.UTF8.GetBytes("control"));
        var first = await client.ReceiveAsync();
        Assert.Equal(SecureFrameChannel.TypeControl, first.Type);
        Assert.Equal("control", Encoding.UTF8.GetString(first.Payload));
        await client.SendAsync(SecureFrameChannel.TypeInput, new byte[] { 1, 2 });
        var input = await host.ReceiveAsync();
        Assert.Equal(SecureFrameChannel.TypeInput, input.Type);
        await host.SendAsync(SecureFrameChannel.TypeMedia, new byte[] { 3 });
        await host.SendAsync(SecureFrameChannel.TypeControl, new byte[] { 4 });
        Assert.Equal(SecureFrameChannel.TypeMedia, (await client.ReceiveAsync()).Type);
        Assert.Equal(new byte[] { 4 }, (await client.ReceiveAsync()).Payload);
    }

    [Fact]
    public async Task SecureFrameChannel_TamperAndWrongRoleFailAuthentication()
    {
        await using (var pair = await ConnectedPair.CreateAsync())
        {
            var host = SecureFrameChannel.CreateDirectional(pair.Server, Secret, Salt, Info, SessionRole.Host);
            var client = SecureFrameChannel.CreateDirectional(new MutatingStream(pair.Client), Secret, Salt, Info, SessionRole.Client);
            await client.SendAsync(SecureFrameChannel.TypeControl, new byte[] { 9 });
            await Assert.ThrowsAnyAsync<CryptographicException>(() => host.ReceiveAsync());
        }
        await using var swapped = await ConnectedPair.CreateAsync();
        var bothHost = SecureFrameChannel.CreateDirectional(swapped.Server, Secret, Salt, Info, SessionRole.Host);
        var otherHost = SecureFrameChannel.CreateDirectional(swapped.Client, Secret, Salt, Info, SessionRole.Host);
        await bothHost.SendAsync(SecureFrameChannel.TypeControl, new byte[] { 1 });
        await Assert.ThrowsAnyAsync<CryptographicException>(() => otherHost.ReceiveAsync());
    }

    [Fact]
    public void HostStore_CreatesUniqueSixDigitIdsAndUpdatesAccess()
    {
        var path = Path.Combine(Path.GetTempPath(), "remote-eternal-qa-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new RemoteEternal.Server.HostStore(path);
            var id1 = store.CreateHost("QA-PC", "Windows 11");
            var id2 = store.CreateHost("QA-Laptop", "Windows 10");
            Assert.Matches("^[0-9]{6}$", id1);
            Assert.Matches("^[0-9]{6}$", id2);
            Assert.NotEqual(id1, id2);
            Assert.True(store.Exists(id1));
            Assert.False(store.Exists("000000"));

            var doc = store.Get(id1);
            Assert.NotNull(doc);
            Assert.Equal("QA-PC", doc!.DeviceName);
            // LiteDB converte string vazia para null (EmptyStringToNull); host recém-registrado ainda não anunciou acesso.
            Assert.Null(doc.AccessMode);

            var salt = Convert.ToBase64String(PasswordHasher.GenerateSalt());
            var verifier = Convert.ToBase64String(PasswordHasher.Compute(Convert.FromBase64String(salt), "qa-host-password"));
            Assert.True(store.UpdateAccess(id1, HostAccess.Unassisted, salt, verifier, "QA-PC", "Windows 11"));
            var updated = store.Get(id1);
            Assert.NotNull(updated);
            Assert.Equal(HostAccess.Unassisted, updated!.AccessMode);
            Assert.Equal(salt, updated.Salt);
            Assert.Equal(verifier, updated.Verifier);

            Assert.False(store.UpdateAccess("000000", HostAccess.Assisted, null, null, "x", "y"));
        }
        finally { TryDelete(path); }
    }

    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes(SecureFrameChannel.SessionSaltV1);
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("re-session");
    internal static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private sealed class FragmentedStream(byte[] data, int fragment) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var n = Math.Min(fragment, buffer.Length); return base.ReadAsync(buffer[..n], cancellationToken); }
    }

    private sealed class MutatingStream(Stream inner) : Stream
    {
        private int writes;
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => inner.CanWrite; public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct); public override int Read(byte[] b, int o, int c) => inner.Read(b,o,c); public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct=default) => inner.ReadAsync(b,ct); public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b,int o,int c) { var copy=b[o..(o+c)]; Mutate(copy); inner.Write(copy); } public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct=default) { var copy=b.ToArray(); Mutate(copy); return inner.WriteAsync(copy,ct); }
        private void Mutate(byte[] data) { if (++writes == 4 && data.Length > 0) data[0] ^= 0x40; }
    }

    private sealed class ConnectedPair : IAsyncDisposable
    {
        public required NetworkStream Server { get; init; } public required NetworkStream Client { get; init; } private TcpClient ServerTcp { get; init; } = null!; private TcpClient ClientTcp { get; init; } = null!; private TcpListener Listener { get; init; } = null!;
        public static async Task<ConnectedPair> CreateAsync() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port=((IPEndPoint)listener.LocalEndpoint).Port; var c=new TcpClient(); var connect=c.ConnectAsync(IPAddress.Loopback,port); var s=await listener.AcceptTcpClientAsync(); await connect; listener.Stop(); return new ConnectedPair { Server=s.GetStream(), Client=c.GetStream(), ServerTcp=s, ClientTcp=c, Listener=listener }; }
        public ValueTask DisposeAsync() { ServerTcp.Dispose(); ClientTcp.Dispose(); Listener.Stop(); return ValueTask.CompletedTask; }
    }
}
