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

/// <summary>
/// Запросы одного соединения выполняются параллельно: медленный ListArchives (облако) не держит в очереди
/// остальные запросы GUI — раньше из-за этого «пустели» все вкладки, пока не ответит MEGA.
/// </summary>
public class IpcConcurrencyTests
{
    [Fact]
    public async Task Slow_Request_Does_Not_Block_Other_Requests_On_Same_Connection()
    {
        var pipeName = "ebk-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new InMemoryHandlers { OnListArchives = ct => release.Task.WaitAsync(ct) };

        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            try
            {
                await IpcConnection.ServeAsync(server, handlers,
                    () => ClientIdentity.ToCaller(ClientIdentity.Resolve(server)), cts.Token);
            }
            catch (OperationCanceledException) { }
        });

        using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await clientStream.ConnectAsync(cts.Token);
        using var client = await IpcClient.StartAsync(clientStream, cts.Token);

        var slow = client.ListArchivesAsync("cloud", includeEncryption: true, cts.Token);
        await Task.Delay(100, cts.Token); // ListArchives уже висит на стороне службы

        // Быстрый запрос отвечает, пока медленный ещё не завершён.
        var hello = await client.HelloAsync(new HelloRequest { ClientBuild = "test" }, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("test-instance", hello.ServiceInstanceId);
        Assert.False(slow.IsCompleted);

        release.SetResult();
        Assert.Empty(await slow.WaitAsync(TimeSpan.FromSeconds(5)));

        cts.Cancel();
        try { await serverTask; } catch { }
    }
}
