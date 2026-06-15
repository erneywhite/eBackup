using System;
using System.IO;
using eBackup.Core.Security;
using eBackup.Security;
using Xunit;

namespace eBackup.Tests;

public sealed class MachineKeyProtectorTests : IDisposable
{
    private readonly string _dir;
    private readonly MachineKeyProtector _p;

    public MachineKeyProtectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ebk-mk-{Guid.NewGuid():N}");
        _p = new MachineKeyProtector(new MachineKeyStore(Path.Combine(_dir, "machine.key")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Decode(string s) => Convert.FromBase64String(s);
    private static string Encode(byte[] b) => Convert.ToBase64String(b);

    [Fact]
    public void RoundTrip_With_Aad()
    {
        var aad = SecretAad.For(SecretScope.Storage, "nas", SecretField.Password);
        var blob = _p.Protect("hunter2", aad);
        Assert.Equal(SecretScheme.MachineKeyV1, SecretSchemeDetector.Detect(blob));
        Assert.Equal("hunter2", _p.Unprotect(blob, aad));
    }

    [Fact]
    public void RoundTrip_Without_Aad()
    {
        var blob = _p.Protect("secret");
        Assert.Equal("secret", _p.Unprotect(blob));
    }

    [Fact]
    public void Empty_Plaintext_Has_No_Blob()
    {
        Assert.Equal("", _p.Protect("", "a"));
        Assert.Equal("", _p.Unprotect("", "a"));
    }

    [Fact]
    public void Two_Protects_Of_Same_Plaintext_Differ()
        => Assert.NotEqual(_p.Protect("x", "a"), _p.Protect("x", "a"));

    [Fact]
    public void Wrong_Aad_Is_Rejected()
    {
        var blob = _p.Protect("p", SecretAad.For(SecretScope.Storage, "A", SecretField.Password));
        Assert.Throws<InvalidDataException>(
            () => _p.Unprotect(blob, SecretAad.For(SecretScope.Storage, "B", SecretField.Password)));
    }

    [Fact]
    public void Tampered_Magic_Is_Rejected()
    {
        var b = Decode(_p.Protect("p", "a"));
        b[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => _p.Unprotect(Encode(b), "a"));
    }

    [Fact]
    public void Tampered_Ciphertext_Is_Rejected()
    {
        var b = Decode(_p.Protect("payload-here", "a"));
        b[^1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => _p.Unprotect(Encode(b), "a"));
    }

    [Fact]
    public void Unknown_KeyId_Is_Unavailable()
    {
        var b = Decode(_p.Protect("p", "a"));
        Guid.NewGuid().ToByteArray().CopyTo(b, 6); // keyId на смещении 6..22
        Assert.Throws<MachineKeyUnavailableException>(() => _p.Unprotect(Encode(b), "a"));
    }

    [Fact]
    public void Not_Base64_Is_Rejected()
        => Assert.Throws<InvalidDataException>(() => _p.Unprotect("not!!!base64$$$", "a"));
}
