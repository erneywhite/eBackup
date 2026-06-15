using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Transport;
using Xunit;

namespace eBackup.Tests.Ipc;

public class IpcTransportTests
{
    private static readonly IpcJsonContext Ctx = IpcJsonContext.Default;

    private static byte[] Preamble()
    {
        var b = new byte[IpcContractInfo.Preamble.Length + 1];
        IpcContractInfo.Preamble.CopyTo(b, 0);
        b[^1] = IpcContractInfo.EnvelopeFormat;
        return b;
    }

    [Fact]
    public async Task Preamble_And_Frames_RoundTrip()
    {
        var f1 = new Frame
        {
            Kind = FrameKinds.Req, Id = "1", Op = IpcOps.Hello,
            Body = JsonSerializer.SerializeToElement(new HelloRequest { ClientBuild = "test" }, Ctx.HelloRequest),
        };
        var f2 = new Frame { Kind = FrameKinds.Ping, Id = "2" };

        var ms = new MemoryStream();
        using (var w = new FrameWriter(ms))
        {
            await w.WritePreambleAsync();
            await w.WriteFrameAsync(f1);
            await w.WriteFrameAsync(f2);
        }

        var rs = new MemoryStream(ms.ToArray());
        var r = new FrameReader(rs);
        await r.ReadPreambleAsync();

        var a = await r.ReadFrameAsync();
        var b = await r.ReadFrameAsync();
        var end = await r.ReadFrameAsync();

        Assert.NotNull(a);
        Assert.Equal(IpcOps.Hello, a!.Op);
        Assert.Equal("test", a.Body!.Value.Deserialize(Ctx.HelloRequest)!.ClientBuild);
        Assert.Equal(FrameKinds.Ping, b!.Kind);
        Assert.Null(b.Body);          // у Ping тела нет
        Assert.Null(end);             // чистое закрытие на границе
    }

    [Fact]
    public async Task Bad_Preamble_Throws()
    {
        var ms = new MemoryStream(new byte[] { (byte)'X', (byte)'X', (byte)'X', (byte)'X', 1 });
        var r = new FrameReader(ms);
        await Assert.ThrowsAsync<IpcProtocolException>(() => r.ReadPreambleAsync());
    }

    [Fact]
    public async Task Oversized_Length_Throws_Before_Allocation()
    {
        var ms = new MemoryStream();
        ms.Write(Preamble());
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, 1_000_000); // объявленная длина >> лимита читателя
        ms.Write(len);
        // тело НЕ пишем: читатель должен отвергнуть по префиксу, не аллоцируя
        ms.Position = 0;

        var r = new FrameReader(ms, maxFrameBytes: 64);
        await r.ReadPreambleAsync();
        await Assert.ThrowsAsync<IpcProtocolException>(() => r.ReadFrameAsync());
    }

    [Fact]
    public async Task Truncated_Payload_Throws()
    {
        var ms = new MemoryStream();
        ms.Write(Preamble());
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, 100); // обещаем 100 байт…
        ms.Write(len);
        ms.Write(new byte[10]);                              // …а даём 10
        ms.Position = 0;

        var r = new FrameReader(ms);
        await r.ReadPreambleAsync();
        await Assert.ThrowsAsync<IpcProtocolException>(() => r.ReadFrameAsync());
    }

    [Fact]
    public async Task Writer_Rejects_Over_Cap_Frame()
    {
        using var w = new FrameWriter(new MemoryStream(), maxFrameBytes: 16);
        var big = new Frame { Kind = FrameKinds.Req, Op = IpcOps.StartBackup, Id = "0123456789" };
        await Assert.ThrowsAsync<IpcProtocolException>(() => w.WriteFrameAsync(big));
    }
}
