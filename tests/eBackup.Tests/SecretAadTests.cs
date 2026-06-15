using System;
using eBackup.Core.Security;
using Xunit;

namespace eBackup.Tests;

public class SecretAadTests
{
    [Fact]
    public void For_Builds_Canonical_String()
        => Assert.Equal("Storage/nas/Password",
            SecretAad.For(SecretScope.Storage, "nas", SecretField.Password));

    [Fact]
    public void Different_Field_Gives_Different_Aad()
        => Assert.NotEqual(
            SecretAad.For(SecretScope.Storage, "nas", SecretField.Password),
            SecretAad.For(SecretScope.Storage, "nas", SecretField.RefreshToken));

    [Fact]
    public void Different_Scope_Gives_Different_Aad()
        => Assert.NotEqual(
            SecretAad.For(SecretScope.Storage, "x", SecretField.Passphrase),
            SecretAad.For(SecretScope.Schedule, "x", SecretField.Passphrase));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_Id_Throws(string? id)
        => Assert.Throws<ArgumentException>(() => SecretAad.For(SecretScope.Storage, id!, SecretField.Password));
}
