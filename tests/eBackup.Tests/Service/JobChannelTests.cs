using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Service.Jobs;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class JobChannelTests : IDisposable
{
    private readonly string _dir;
    private readonly HistoryStore _history;

    public JobChannelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ebk-ch-{Guid.NewGuid():N}");
        _history = new HistoryStore(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Emit_Assigns_Increasing_Seq_Aligned_To_Journal_Lines()
    {
        var ch = new JobChannel(_history, "run1");
        Assert.Equal(1, ch.Emit(JobNoteKind.Log, "a", 0, null));
        Assert.Equal(2, ch.Emit(JobNoteKind.Log, "b", 0, null));

        var lines = _history.ReadLogFromSeq("run1", 0, 100);
        Assert.Equal(2, lines.Count);
        Assert.Equal(1, lines[0].Seq);          // seq == номер строки журнала
        Assert.Contains("a", lines[0].Text);
    }

    [Fact]
    public async Task Subscribe_Returns_Backlog_After_FromSeq_And_Streams_Live()
    {
        var ch = new JobChannel(_history, "run1");
        ch.Emit(JobNoteKind.Log, "old1", 0, null);
        ch.Emit(JobNoteKind.Log, "old2", 0, null);

        var sub = ch.Subscribe(fromSeq: 1);     // после seq 1 → только old2
        Assert.Single(sub.Backlog);
        Assert.Equal(2, sub.Backlog[0].Seq);

        ch.Emit(JobNoteKind.Log, "live1", 0, null);
        var note = await sub.Live.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("live1", note.Text);

        sub.Dispose();
    }

    [Fact]
    public async Task Complete_Ends_The_Live_Stream()
    {
        var ch = new JobChannel(_history, "run1");
        var sub = ch.Subscribe(0);
        ch.Complete();
        Assert.False(await sub.Live.WaitToReadAsync()); // завершён и пуст
    }

    [Fact]
    public async Task Late_Subscribe_After_Complete_Gets_Backlog_Then_Closed()
    {
        var ch = new JobChannel(_history, "run1");
        ch.Emit(JobNoteKind.Log, "x", 0, null);
        ch.Complete();

        var sub = ch.Subscribe(0);
        Assert.Single(sub.Backlog);                      // бэклог из кольца доступен
        Assert.False(await sub.Live.WaitToReadAsync());  // живых нот уже не будет
    }
}
