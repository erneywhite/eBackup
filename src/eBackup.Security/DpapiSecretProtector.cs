using System.Security.Cryptography;
using System.Text;
using eBackup.Core.Security;

namespace eBackup.Security;

/// <summary>
/// Шифрование секретов через Windows DPAPI (<see cref="DataProtectionScope.CurrentUser"/>).
///
/// Секреты привязаны к текущему Windows-пользователю на ЭТОЙ машине и намеренно
/// НЕ переносятся на другой ПК: при восстановлении на новой машине параметры
/// подключения вводятся заново. Это нормально — учётки не должны «уезжать» в архивах.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Дополнительная энтропия — слегка усложняет расшифровку посторонними процессами
    // того же пользователя. НЕ заменяет секрет: это лишь добавочный фактор DPAPI.
    private static readonly byte[] Entropy = "eBackup.v1"u8.ToArray();

    public string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string protectedValue)
    {
        var encrypted = Convert.FromBase64String(protectedValue);
        var data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}
