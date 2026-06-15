using System;
using System.IO;
using System.Security.Cryptography;
using eBackup.Core.Security;
using eBackup.Security;
using Xunit;

namespace eBackup.Tests;

public sealed class SecretMigratorTests : IDisposable
{
    private readonly string _dir;
    private readonly MachineKeyProtector _machine;
    private readonly DpapiSecretProtector _legacy = new();
    private const string Aad = "Storage/nas/Password";

    public SecretMigratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ebk-mig-{Guid.NewGuid():N}");
        _machine = new MachineKeyProtector(new MachineKeyStore(Path.Combine(_dir, "machine.key")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Легаси-протектор, который не может расшифровать (имитирует чужой профиль).
    private sealed class ThrowingLegacy : ISecretProtector
    {
        public string Protect(string plaintext) => throw new NotSupportedException();
        public string Unprotect(string protectedValue) => throw new CryptographicException("чужой профиль");
    }

    [Fact]
    public void Legacy_Field_Is_Migrated_To_MachineKey()
    {
        var legacyBlob = _legacy.Protect("hunter2");
        var r = SecretMigrator.MigrateField(legacyBlob, _legacy, _machine, Aad);

        Assert.Equal(SecretMigrationOutcome.Migrated, r.Outcome);
        Assert.Equal(SecretScheme.MachineKeyV1, SecretSchemeDetector.Detect(r.Value));
        Assert.Equal("hunter2", _machine.Unprotect(r.Value!, Aad)); // расшифровывается машинным ключом
    }

    [Fact]
    public void Already_MachineKey_Is_NotNeeded()
    {
        var blob = _machine.Protect("x", Aad);
        var r = SecretMigrator.MigrateField(blob, _legacy, _machine, Aad);
        Assert.Equal(SecretMigrationOutcome.NotNeeded, r.Outcome);
        Assert.Equal(blob, r.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_Is_NotNeeded(string? value)
    {
        var r = SecretMigrator.MigrateField(value, _legacy, _machine, Aad);
        Assert.Equal(SecretMigrationOutcome.NotNeeded, r.Outcome);
    }

    [Fact]
    public void Undecryptable_Legacy_Needs_Reentry_And_Keeps_Original()
    {
        var legacyBlob = Convert.ToBase64String(new byte[80]); // выглядит как легаси DPAPI
        var r = SecretMigrator.MigrateField(legacyBlob, new ThrowingLegacy(), _machine, Aad);

        Assert.Equal(SecretMigrationOutcome.NeedsReentry, r.Outcome);
        Assert.Equal(legacyBlob, r.Value); // исходный блоб не тронут — данные не потеряны
    }

    [Fact]
    public void Corrupt_Field_Throws()
        => Assert.Throws<InvalidDataException>(
            () => SecretMigrator.MigrateField("not!!!base64$$$", _legacy, _machine, Aad));
}
