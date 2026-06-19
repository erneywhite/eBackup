using System;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests;

public class MegaSessionTests
{
    [Fact]
    public void Deserialize_RoundTrips_SessionId_And_MasterKey()
    {
        // Форма, которую пишет MegaSession.ConnectAsync: {"SessionId":..,"MasterKeyB64":..(base64)}.
        var masterKey = new byte[] { 1, 2, 3, 4, 250, 251 };
        var json = $"{{\"SessionId\":\"sess-abc-123\",\"MasterKeyB64\":\"{Convert.ToBase64String(masterKey)}\"}}";

        var token = MegaSession.Deserialize(json);

        Assert.Equal("sess-abc-123", token.SessionId);
        Assert.Equal(masterKey, token.MasterKey);
    }
}
