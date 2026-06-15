using System;
using eBackup.Core.Security;
using Xunit;

namespace eBackup.Tests;

public class SecretSchemeDetectorTests
{
    // Собрать корректный EBKS-блоб нужной длины (можно подкрутить scheme/ver/aadLen для негативных кейсов).
    private static string MakeEbks(byte scheme = SecretSchemeDetector.SchemeMachineKeyV1,
                                   byte ver = SecretSchemeDetector.FormatVersion,
                                   int aadLen = 4, int ctLen = 8)
    {
        var b = new byte[24 + aadLen + 16 + 12 + 16 + ctLen];
        b[0] = (byte)'E'; b[1] = (byte)'B'; b[2] = (byte)'K'; b[3] = (byte)'S';
        b[4] = scheme;
        b[5] = ver;
        // keyId b[6..22] — нули, не важно для детектора
        b[22] = (byte)((aadLen >> 8) & 0xFF);
        b[23] = (byte)(aadLen & 0xFF);
        return Convert.ToBase64String(b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Values_Are_Empty(string? value)
        => Assert.Equal(SecretScheme.Empty, SecretSchemeDetector.Detect(value));

    [Fact]
    public void Valid_Ebks_Blob_Is_MachineKey()
        => Assert.Equal(SecretScheme.MachineKeyV1, SecretSchemeDetector.Detect(MakeEbks()));

    [Fact]
    public void Valid_Ebks_With_Empty_Ciphertext_Is_MachineKey()
        => Assert.Equal(SecretScheme.MachineKeyV1, SecretSchemeDetector.Detect(MakeEbks(ctLen: 0)));

    [Fact]
    public void Legacy_Dpapi_Base64_Is_Legacy()
    {
        // Сырой base64 «как DPAPI»: не начинается с EBKS, правдоподобная длина.
        var blob = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(new string('\x01', 60)));
        Assert.Equal(SecretScheme.LegacyDpapi, SecretSchemeDetector.Detect(blob));
    }

    [Fact]
    public void Legacy_Dpapi_Is_Never_MachineKey()
    {
        var blob = Convert.ToBase64String(new byte[120]); // нули → точно не «EBKS»
        Assert.NotEqual(SecretScheme.MachineKeyV1, SecretSchemeDetector.Detect(blob));
        Assert.Equal(SecretScheme.LegacyDpapi, SecretSchemeDetector.Detect(blob));
    }

    [Fact]
    public void Non_Base64_Junk_Is_Unknown()
        => Assert.Equal(SecretScheme.Unknown, SecretSchemeDetector.Detect("not!!!base64$$$"));

    [Fact]
    public void Ebks_Magic_But_Too_Short_Is_Unknown()
    {
        var b = new byte[] { (byte)'E', (byte)'B', (byte)'K', (byte)'S', 0x01, 0x01 }; // нет места под keyId/aadLen
        Assert.Equal(SecretScheme.Unknown, SecretSchemeDetector.Detect(Convert.ToBase64String(b)));
    }

    [Fact]
    public void Ebks_Unknown_Scheme_Is_Unknown()
        => Assert.Equal(SecretScheme.Unknown, SecretSchemeDetector.Detect(MakeEbks(scheme: 0x02)));

    [Fact]
    public void Ebks_Newer_Version_Is_Unknown()
        => Assert.Equal(SecretScheme.Unknown, SecretSchemeDetector.Detect(MakeEbks(ver: 2)));

    [Fact]
    public void Ebks_With_Inconsistent_Length_Is_Unknown()
    {
        // Физически короткий блоб, но aadLen объявлен огромным → структура неконсистентна.
        var b = new byte[30];
        b[0] = (byte)'E'; b[1] = (byte)'B'; b[2] = (byte)'K'; b[3] = (byte)'S';
        b[4] = SecretSchemeDetector.SchemeMachineKeyV1;
        b[5] = SecretSchemeDetector.FormatVersion;
        b[22] = 0x13; b[23] = 0x88; // aadLen = 5000, а в массиве всего 30 байт
        Assert.Equal(SecretScheme.Unknown, SecretSchemeDetector.Detect(Convert.ToBase64String(b)));
    }
}
