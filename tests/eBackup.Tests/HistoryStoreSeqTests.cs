using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.History;
using Xunit;

namespace eBackup.Tests;

public sealed class HistoryStoreSeqTests : IDisposable
{
    private readonly string _dir;
    private readonly HistoryStore _store;

    public HistoryStoreSeqTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ebk-hist-{Guid.NewGuid():N}");
        _store = new HistoryStore(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ReadLogFromSeq_Returns_Correct_Ranges()
    {
        for (var i = 1; i <= 5; i++) _store.AppendLog("run1", $"line-{i}");

        var all = _store.ReadLogFromSeq("run1", 0, 100);
        Assert.Equal(5, all.Count);
        Assert.Equal(1, all[0].Seq);
        Assert.Equal(5, all[^1].Seq);
        Assert.Contains("line-1", all[0].Text);

        var tail = _store.ReadLogFromSeq("run1", 2, 100); // после seq 2 → строки 3..5
        Assert.Equal(3, tail.Count);
        Assert.Equal(3, tail[0].Seq);
        Assert.Contains("line-3", tail[0].Text);

        Assert.Equal(2, _store.ReadLogFromSeq("run1", 0, 2).Count);   // лимит maxLines
        Assert.Empty(_store.ReadLogFromSeq("nope", 0, 100));          // нет такого лога
        Assert.Empty(_store.ReadLogFromSeq("run1", 99, 100));         // fromSeq за пределами
    }

    [Fact]
    public async Task Additive_Fields_RoundTrip()
    {
        await _store.SaveRunAsync(new BackupRunRecord
        {
            Id = "r1",
            StartedAt = DateTimeOffset.Now,
            State = "Running",
            LastSeq = 42,
            OwnerSid = "S-1-5-21-1",
        });

        var loaded = (await _store.LoadAsync()).Single(r => r.Id == "r1");
        Assert.Equal("Running", loaded.State);
        Assert.Equal(42, loaded.LastSeq);
        Assert.Equal("S-1-5-21-1", loaded.OwnerSid);
    }

    [Fact]
    public async Task Old_Record_Without_New_Fields_Loads_As_Defaults()
    {
        // «Старый» runs.json (формат 1.1, без новых полей) должен грузиться без падений.
        var runsJson = """[{"Id":"old1","StartedAt":"2025-01-01T00:00:00+00:00","Success":true}]""";
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "runs.json"), runsJson);

        var loaded = (await _store.LoadAsync()).Single(r => r.Id == "old1");
        Assert.Null(loaded.State);
        Assert.Equal(0, loaded.LastSeq);
        Assert.Null(loaded.OwnerSid);
        Assert.True(loaded.Success);
    }
}
