using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using Xunit;

namespace eBackup.Tests.Ipc;

public class IpcDispatcherTests
{
    private static readonly IpcJsonContext Ctx = IpcJsonContext.Default;
    private readonly InMemoryHandlers _h = new();

    private static Frame Req<T>(string id, string op, T body, JsonTypeInfo<T> ti)
        => new() { Kind = FrameKinds.Req, Id = id, Op = op, Body = JsonSerializer.SerializeToElement(body, ti) };

    private static Frame ReqNoBody(string id, string op) => new() { Kind = FrameKinds.Req, Id = id, Op = op };

    private Task<Frame> Dispatch(Frame f) => IpcDispatcher.DispatchAsync(f, _h, CallerContext.Test, CancellationToken.None);

    private static IpcError Err(Frame f) => f.Body!.Value.Deserialize(Ctx.IpcError)!;

    [Fact]
    public async Task Hello_Returns_Response()
    {
        var resp = await Dispatch(Req("1", IpcOps.Hello, new HelloRequest { ClientBuild = "c" }, Ctx.HelloRequest));
        Assert.Equal(FrameKinds.Resp, resp.Kind);
        Assert.Equal("1", resp.Id);
        Assert.Equal("test-instance", resp.Body!.Value.Deserialize(Ctx.HelloResponse)!.ServiceInstanceId);
    }

    [Fact]
    public async Task StartBackup_Decodes_Body_And_Responds()
    {
        var req = new StartBackupRequest { ModuleIds = ["obs"], Trigger = "t", ClientRequestId = "r1" };
        var resp = await Dispatch(Req("2", IpcOps.StartBackup, req, Ctx.StartBackupRequest));

        Assert.Equal(FrameKinds.Resp, resp.Kind);
        Assert.Equal("job-1", resp.Body!.Value.Deserialize(Ctx.StartBackupResponse)!.JobId);
        Assert.Equal(["obs"], _h.LastStartBackup!.ModuleIds); // тело реально дошло до обработчика
    }

    [Fact]
    public async Task Handler_Fault_Maps_To_Typed_Fault()
    {
        var resp = await Dispatch(Req("3", IpcOps.GetJob, new GetJobRequest { JobId = "missing" }, Ctx.GetJobRequest));
        Assert.Equal(FrameKinds.Fault, resp.Kind);
        Assert.Equal(IpcErrorCodes.NotFound, Err(resp).Code);
    }

    [Fact]
    public async Task Unknown_Op_Is_Unsupported_Fault()
    {
        var resp = await Dispatch(ReqNoBody("4", "futureOp"));
        Assert.Equal(FrameKinds.Fault, resp.Kind);
        Assert.Equal(IpcErrorCodes.Unsupported, Err(resp).Code);
    }

    [Fact]
    public async Task Non_Request_Frame_Is_BadRequest()
    {
        var resp = await Dispatch(new Frame { Kind = FrameKinds.Note, Op = IpcNotes.Log });
        Assert.Equal(FrameKinds.Fault, resp.Kind);
        Assert.Equal(IpcErrorCodes.BadRequest, Err(resp).Code);
    }

    [Fact]
    public async Task Missing_Body_Is_BadRequest()
    {
        var resp = await Dispatch(ReqNoBody("5", IpcOps.StartBackup));
        Assert.Equal(FrameKinds.Fault, resp.Kind);
        Assert.Equal(IpcErrorCodes.BadRequest, Err(resp).Code);
    }
}
