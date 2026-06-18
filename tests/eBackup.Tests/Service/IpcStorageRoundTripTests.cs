using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.History;
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

public sealed class IpcStorageRoundTripTests : IDisposable
{
    private readonly string _root;

    public IpcStorageRoundTripTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-iprt-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Upsert_List_Delete_Storage_Over_Real_Pipe()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        await using var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.2.0");

        var pipeName = "ebk-iprt-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            await IpcConnection.ServeAsync(server, handlers,
                () => ClientIdentity.ToCaller(ClientIdentity.Resolve(server)), cts.Token);
        });

        using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await clientStream.ConnectAsync(cts.Token);
        using var client = await IpcClient.StartAsync(clientStream, cts.Token);

        // GUI-клиент отдаёт открытый секрет → служба шифрует машинным ключом
        await client.UpsertStorageAsync(new StorageInput
        {
            Id = "nas", Name = "NAS", Kind = "Sftp",
            Settings = new() { ["host"] = "h", ["port"] = "2022" },
            PlaintextSecrets = new() { ["password"] = "hunter2" },
        }, cts.Token);

        var nas = Assert.Single(await client.ListStoragesAsync(cts.Token));
        Assert.Equal("NAS", nas.Name);
        Assert.True(nas.HasSecret);

        // на диске — под машинным ключом, служба расшифровывает обратно
        var saved = (await storages.LoadAsync()).Single(s => s.Id == "nas");
        Assert.Equal("hunter2", storages.Unprotect(saved.ProtectedPassword!));

        await client.DeleteStorageAsync("nas", cts.Token);
        Assert.Empty(await client.ListStoragesAsync(cts.Token));

        cts.Cancel();
        try { await serverTask; } catch { }
    }
}
