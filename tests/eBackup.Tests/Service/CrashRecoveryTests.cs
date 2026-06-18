using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Service.Jobs;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class CrashRecoveryTests : IDisposable
{
    private readonly string _dir;
    private readonly HistoryStore _history;

    public CrashRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ebk-crash-{Guid.NewGuid():N}");
        _history = new HistoryStore(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Sweep_Marks_Unfinished_As_Interrupted_Leaves_Done_Alone()
    {
        await _history.SaveRunAsync(new BackupRunRecord { Id = "running", StartedAt = DateTimeOffset.Now, State = "Running" });
        await _history.SaveRunAsync(new BackupRunRecord { Id = "queued", StartedAt = DateTimeOffset.Now, State = "Queued" });
        await _history.SaveRunAsync(new BackupRunRecord { Id = "old", StartedAt = DateTimeOffset.Now }); // State=null, Success=null, не завершён
        await _history.SaveRunAsync(new BackupRunRecord { Id = "done", StartedAt = DateTimeOffset.Now, FinishedAt = DateTimeOffset.Now, State = "Completed", Success = true });

        var fixedCount = await CrashRecovery.SweepInterruptedAsync(_history);
        Assert.Equal(3, fixedCount);

        var runs = await _history.LoadAsync();
        foreach (var id in new[] { "running", "queued", "old" })
        {
            var r = runs.Single(x => x.Id == id);
            Assert.Equal("Interrupted", r.State);
            Assert.False(r.Success);
            Assert.NotNull(r.FinishedAt);
        }

        var done = runs.Single(x => x.Id == "done");
        Assert.Equal("Completed", done.State); // завершённый не тронут
        Assert.True(done.Success);
    }

    [Fact]
    public async Task Sweep_Is_Idempotent()
    {
        await _history.SaveRunAsync(new BackupRunRecord { Id = "r", StartedAt = DateTimeOffset.Now, State = "Running" });
        Assert.Equal(1, await CrashRecovery.SweepInterruptedAsync(_history));
        Assert.Equal(0, await CrashRecovery.SweepInterruptedAsync(_history)); // второй проход — нечего чинить
    }
}
