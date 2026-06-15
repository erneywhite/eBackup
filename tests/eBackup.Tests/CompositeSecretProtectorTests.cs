using System;
using System.IO;
using eBackup.Core.Security;
using eBackup.Security;
using Xunit;

namespace eBackup.Tests;

public sealed class CompositeSecretProtectorTests : IDisposable
{
    private readonly string _dir;
    private readonly CompositeSecretProtector _composite;

    public CompositeSecretProtectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ebk-comp-{Guid.NewGuid():N}");
        var machine = new MachineKeyProtector(new MachineKeyStore(Path.Combine(_dir, "machine.key")));
        _composite = new CompositeSecretProtector(machine, new DpapiSecretProtector());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Protect_Always_Writes_MachineKey()
    {
        var blob = _composite.Protect("p", "a");
        Assert.Equal(SecretScheme.MachineKeyV1, SecretSchemeDetector.Detect(blob));
        Assert.Equal("p", _composite.Unprotect(blob, "a"));
    }

    [Fact]
    public void Reads_Legacy_Dpapi_Blob()
    {
        // Легаси-блоб создаём прежним протектором (Protect ещё рабочий в дормантной сборке).
        var legacyBlob = new DpapiSecretProtector().Protect("oldpass");
        Assert.Equal(SecretScheme.LegacyDpapi, SecretSchemeDetector.Detect(legacyBlob));
        Assert.Equal("oldpass", _composite.Unprotect(legacyBlob));
    }

    [Fact]
    public void Empty_Passes_Through()
        => Assert.Equal("", _composite.Unprotect("", "a"));

    [Fact]
    public void Corrupt_Value_Throws()
        => Assert.Throws<InvalidDataException>(() => _composite.Unprotect("not!!!base64$$$", "a"));
}
