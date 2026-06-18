using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using Xunit;

namespace eBackup.Tests.Ipc;

public class IpcPipeIntegrationTests
{
    [Fact]
    public async Task RoundTrips_Over_Real_Named_Pipe()
    {
        var pipeName = "ebk-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var handlers = new InMemoryHandlers();
        var identityTcs = new TaskCompletionSource<ClientIdentity.Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            await IpcConnection.ServeAsync(server, handlers, () =>
            {
                var id = ClientIdentity.Resolve(server); // после чтения преамбулы клиента
                identityTcs.TrySetResult(id);
                return ClientIdentity.ToCaller(id);
            }, cts.Token);
        });

        // Impersonation-уровень обязателен, иначе RunAsClient на сервере увидит ANONYMOUS.
        using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await clientStream.ConnectAsync(cts.Token);
        using var client = await IpcClient.StartAsync(clientStream, cts.Token);

        // Hello
        var hello = await client.HelloAsync(new HelloRequest { ClientBuild = "test" }, cts.Token);
        Assert.Equal("test-instance", hello.ServiceInstanceId);

        // StartBackup — тело реально доходит до обработчика на той стороне пайпа
        var start = await client.StartBackupAsync(
            new StartBackupRequest { ModuleIds = ["obs"], ClientRequestId = "r1" }, cts.Token);
        Assert.Equal("job-1", start.JobId);
        Assert.Equal(["obs"], handlers.LastStartBackup!.ModuleIds);

        // Ошибка обработчика → типизированный fault на клиенте
        var ex = await Assert.ThrowsAsync<IpcRequestException>(
            () => client.GetJobAsync(new GetJobRequest { JobId = "missing" }, cts.Token));
        Assert.Equal(IpcErrorCodes.NotFound, ex.Error.Code);

        // Личность клиента определена через RunAsClient
        var identity = await identityTcs.Task.WaitAsync(cts.Token);
        Assert.StartsWith("S-1-", identity.Sid);
        Assert.True(identity.IsAcceptable); // обычный локальный пользователь принимается

        client.Dispose();
        clientStream.Dispose();
        cts.Cancel();
        try { await serverTask; } catch { /* отменён/закрыт — ок */ }
    }
}
