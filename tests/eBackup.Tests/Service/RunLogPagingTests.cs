using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Ipc.Transport;
using eBackup.Security;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

/// <summary>
/// GetRunLog режет страницу и по объёму: журнал крупного бэкапа (длинные пути OBS, кириллица → \uXXXX в JSON)
/// не должен превышать лимит фрейма IPC, иначе служба рвёт соединение («соединение со службой закрыто»).
/// </summary>
public sealed class RunLogPagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ebk-runlog-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly CallerContext Caller = new("S-1-5-21-1", IsAdmin: false);

    [Fact]
    public async Task Long_Cyrillic_Log_Pages_Stay_Under_Frame_Limit_And_Cover_Every_Line()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        await using var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.5.2");

        // 5000 строк как в журнале OBS: длинный путь + кириллица (≈6 байт на букву в JSON).
        const int total = 5000;
        var lines = Enumerable.Range(1, total).Select(i =>
            $"[12:00:{i % 60:00}.{i % 1000:000}]   + data/obs/obs-studio/plugin_config/obs-browser/Профиль_браузера_{i}/" +
            $"Кэш_и_прочие_данные_с_очень_длинным_именем_файла_{i}.bin (1,2 КБ) · sha256 0123456789ab…").ToArray();
        var path = history.LogPath("run1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, lines);

        var got = new List<LogLine>();
        long seq = 0;
        var pages = 0;
        while (true)
        {
            var resp = await handlers.GetRunLogAsync(
                new GetRunLogRequest { RunId = "run1", FromSeq = seq, MaxLines = 5000 }, Caller, CancellationToken.None);
            pages++;

            // Ровно так ответ уходит по проводу (IpcDispatcher.Resp + FrameWriter).
            var frame = new Frame
            {
                Kind = FrameKinds.Resp, Id = "1",
                Body = JsonSerializer.SerializeToElement(resp, IpcJsonContext.Default.GetRunLogResponse),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, IpcJsonContext.Default.Frame).Length;
            Assert.True(bytes < FrameReader.DefaultMaxFrameBytes, $"страница {pages}: {bytes} байт");

            got.AddRange(resp.Lines);
            seq = resp.NextSeq;
            if (!resp.HasMore || resp.Lines.Length == 0 || pages > 100) break;
        }

        Assert.True(pages > 1, "5000 длинных строк не влезают в одну страницу — бюджет должен был сработать");
        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), got.Select(l => l.Seq));
        Assert.Equal(lines, got.Select(l => l.Text));
    }

    [Fact]
    public async Task Huge_Single_Line_Is_Truncated_Instead_Of_Breaking_The_Frame()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist2"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key2", "machine.key"))),
            Path.Combine(_root, "cfg2", "storages.json"), Path.Combine(_root, "cfg2", "connections.json"));
        await using var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.5.2");

        var path = history.LogPath("run2");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, [new string('Ж', 400_000), "хвост"]);

        var resp = await handlers.GetRunLogAsync(
            new GetRunLogRequest { RunId = "run2", FromSeq = 0, MaxLines = 5000 }, Caller, CancellationToken.None);

        Assert.True(resp.Lines[0].Text.Length < 20_000);
        Assert.EndsWith("…", resp.Lines[0].Text);
    }
}
