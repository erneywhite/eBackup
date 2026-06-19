using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Security;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class IpcScheduleRoundTripTests : IDisposable
{
    private readonly string _root;

    public IpcScheduleRoundTripTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-isrt-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Upsert_List_RunNow_Delete_Schedule_Over_Real_Pipe()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        var schedules = new ScheduleStore(storages.Protector, Path.Combine(_root, "cfg", "schedules.json"));
        await using var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.2.0", schedules: schedules);

        var pipeName = "ebk-isrt-" + Guid.NewGuid().ToString("N");
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

        await client.UpsertScheduleAsync(new ScheduleInput
        {
            Id = "nightly", Name = "Ночной", Kind = "Daily", Hour = 3, Minute = 0,
            ModuleIds = ["obs"], TargetStorageIds = ["nas"],
        }, cts.Token);

        var s = Assert.Single(await client.ListSchedulesAsync(cts.Token));
        Assert.Equal("nightly", s.Id);
        Assert.Equal("Ночной", s.Name);
        Assert.False(s.HasPassphrase);
        Assert.NotNull(s.NextRun);

        // Запустить сейчас → задача поставлена в очередь (origin Scheduled на стороне службы).
        var run = await client.RunScheduleNowAsync("nightly", cts.Token);
        Assert.NotEmpty(run.JobId);

        await client.DeleteScheduleAsync("nightly", cts.Token);
        Assert.Empty(await client.ListSchedulesAsync(cts.Token));

        cts.Cancel();
        try { await serverTask; } catch { }
    }
}
