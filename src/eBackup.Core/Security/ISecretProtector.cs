namespace eBackup.Core.Security;

/// <summary>
/// Шифрует/расшифровывает чувствительные строки (пароли, парольные фразы ключей)
/// для хранения в конфиге. Реализация по умолчанию — Windows DPAPI
/// (см. <c>eBackup.Security.DpapiSecretProtector</c>).
/// </summary>
public interface ISecretProtector
{
    /// <summary>Зашифровать строку в безопасное для хранения представление (например, base64).</summary>
    string Protect(string plaintext);

    /// <summary>Расшифровать значение, ранее полученное из <see cref="Protect"/>.</summary>
    string Unprotect(string protectedValue);
}
