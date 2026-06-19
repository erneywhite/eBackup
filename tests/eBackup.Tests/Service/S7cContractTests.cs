using System.Text.Json;
using eBackup.Ipc.Contracts;
using Xunit;

namespace eBackup.Tests.Service;

public class S7cContractTests
{
    [Fact]
    public void StartRestoreRequest_RoundTrips_With_Passphrase_Ticket()
    {
        var req = new StartRestoreRequest
        {
            SourceStorageId = "nas",
            RemoteName = "a.ebk",
            Passphrase = PassphraseRef.FromTicket("ticket-abc"),
        };

        var json = JsonSerializer.Serialize(req, IpcJsonContext.Default.StartRestoreRequest);
        var back = JsonSerializer.Deserialize(json, IpcJsonContext.Default.StartRestoreRequest)!;

        Assert.Equal("ticket", back.Passphrase!.Kind);
        Assert.Equal("ticket-abc", back.Passphrase.Ticket);
    }

    [Fact]
    public void StartRestoreRequest_Without_Passphrase_Is_Null()
    {
        var req = new StartRestoreRequest { SourceStorageId = "nas", RemoteName = "a.ebk" };
        var json = JsonSerializer.Serialize(req, IpcJsonContext.Default.StartRestoreRequest);
        var back = JsonSerializer.Deserialize(json, IpcJsonContext.Default.StartRestoreRequest)!;
        Assert.Null(back.Passphrase);
    }
}
