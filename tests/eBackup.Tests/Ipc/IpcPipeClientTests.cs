using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Ipc.Client;
using Xunit;

namespace eBackup.Tests.Ipc;

public class IpcPipeClientTests
{
    [Fact]
    public async Task ConnectAsync_Rejects_Server_Not_Owned_By_LocalSystem()
    {
        var pipeName = "ebk-squat-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () => { try { await server.WaitForConnectionAsync(cts.Token); } catch { } });

        // Пайп создан тестовым пользователем (владелец ≠ SYSTEM) → клиент обязан отказать.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => IpcPipeClient.ConnectAsync(pipeName, cts.Token));

        cts.Cancel();
        try { await serverTask; } catch { }
    }
}
