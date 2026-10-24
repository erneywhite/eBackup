using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using Xunit;

namespace eBackup.Tests.Ipc;

/// <summary>
/// Служба закрыла pipe (перезапуск, протокольная ошибка) → клиент помечает себя закрытым, и новые
/// запросы сразу падают с IpcConnectionClosedException, а не висят вечно (ServiceConnection по
/// IsClosed переподключается).
/// </summary>
public class IpcClientClosedTests
{
    [Fact]
    public async Task Requests_After_Server_Drop_Fail_Fast_Instead_Of_Hanging()
    {
        var pipeName = "ebk-test-" + Guid.NewGuid().ToString("N");
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverCts = new CancellationTokenSource();
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(testCts.Token);
            try
            {
                await IpcConnection.ServeAsync(server, new InMemoryHandlers(),
                    () => ClientIdentity.ToCaller(ClientIdentity.Resolve(server)), serverCts.Token);
            }
            catch (OperationCanceledException) { }
        });

        using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await clientStream.ConnectAsync(testCts.Token);
        using var client = await IpcClient.StartAsync(clientStream, testCts.Token);

        await client.HelloAsync(new HelloRequest { ClientBuild = "test" }, testCts.Token);
        Assert.False(client.IsClosed);

        // Служба «ушла»: гасим серверную сторону.
        serverCts.Cancel();
        server.Dispose();
        try { await serverTask; } catch { }

        var sw = Stopwatch.StartNew();
        while (!client.IsClosed && sw.ElapsedMilliseconds < 5000) await Task.Delay(20, testCts.Token);
        Assert.True(client.IsClosed);

        // Раньше такой запрос либо падал на записи, либо висел навсегда — теперь сразу понятная ошибка.
        await Assert.ThrowsAsync<IpcConnectionClosedException>(
            () => client.HelloAsync(new HelloRequest { ClientBuild = "test" }, testCts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
