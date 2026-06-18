using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Abstractions;
using eBackup.Core.History;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Security;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class JobStreamingIntegrationTests : IDisposable
{
    private readonly string _root;

    public JobStreamingIntegrationTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-stream-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class DirModule(string id, string tokenPath) : IBackupModule
    {
        public string Id => id;
        public string DisplayName => id;
        public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PathEntry>>(
                [new PathEntry { TokenPath = tokenPath, Type = PathEntryType.Directory, ArchivePath = "data" }]);
    }

    [Fact]
    public async Task Attach_Streams_Notes_To_Terminal_Over_Real_Pipe()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "f.txt"), "data");

        // Полный стек службы
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var module = new DirModule("test", src.Replace('\\', '/'));
        var runner = new BackupRunner(_ => [module], Path.Combine(_root, "build"));
        var writer = new JobHistoryWriter(history);
        await using var jobs = new JobManager(runner, runId => new JobChannel(history, runId), writer.OnStateChanged);
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.2.0");
        var jobStream = new JobStreamAdapter(jobs);

        // Живой пайп
        var pipeName = "ebk-stream-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            await IpcConnection.ServeAsync(server, handlers,
                () => ClientIdentity.ToCaller(ClientIdentity.Resolve(server)), cts.Token, jobStream);
        });

        using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await clientStream.ConnectAsync(cts.Token);
        using var client = await IpcClient.StartAsync(clientStream, cts.Token);

        var start = await client.StartBackupAsync(
            new StartBackupRequest { ModuleIds = ["test"], Trigger = "t" }, cts.Token);

        // Подписываемся на живой прогресс — собираем ноты до терминального состояния
        var ops = new List<string>();
        string? finalState = null;
        await foreach (var f in client.AttachToJobAsync(start.JobId, 0, cts.Token))
        {
            ops.Add(f.Op ?? "");
            if (f.Op == IpcNotes.State)
                finalState = f.Body!.Value.GetProperty("state").GetString();
        }

        Assert.Equal("Completed", finalState);  // дошли до терминала через стрим
        Assert.Contains(IpcNotes.Log, ops);     // были подробные строки лога
        Assert.Contains(IpcNotes.State, ops);

        cts.Cancel();
        try { await serverTask; } catch { /* отменён/закрыт */ }
    }
}
