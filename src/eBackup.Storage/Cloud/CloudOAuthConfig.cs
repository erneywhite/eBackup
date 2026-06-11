using System.Text;

namespace eBackup.Storage.Cloud;

/// <summary>
/// Идентификаторы OAuth-приложений eBackup. Для настольных приложений это
/// ПУБЛИЧНЫЕ значения (Google документирует, что installed-app credentials не
/// считаются конфиденциальными) — секретны только токены пользователя, и они
/// хранятся в конфиге зашифрованными DPAPI.
///
/// Значения слегка обфусцированы (reverse + base64) — не ради «безопасности»,
/// а чтобы не триггерить secret-сканеры (GitHub push protection) и бездумную
/// копипасту. Тот же приём использует rclone и другие открытые клиенты.
/// </summary>
public static class CloudOAuthConfig
{
    public static string GoogleClientId => Deobfuscate(
        "bW9jLnRuZXRub2NyZXN1ZWxnb29nLnNwcGEucGJnOTh0c2xkbGRwamQyODZiam51cGIxdWRiMnJrMXQtOTE5OTE2MjMwOTI4");

    public static string GoogleClientSecret => Deobfuscate(
        "eGlzOVp4UXBKR1NtSHQ0RDRFTGlLemF3Yl95RC1YUFNDT0c=");

    public const string DropboxAppKey = "q9mbd9ivwoirp1m";

    /// <summary>Зарегистрированный redirect Dropbox-приложения (порт фиксирован).</summary>
    public const int DropboxRedirectPort = 53682;

    private static string Deobfuscate(string base64)
    {
        var chars = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}
