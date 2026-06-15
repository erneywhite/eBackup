namespace eBackup.Core.Security;

/// <summary>Какой схемой зашифрован сохранённый секрет (для маршрутизации расшифровки и миграции).</summary>
public enum SecretScheme
{
    /// <summary>Пустое значение — секрета нет.</summary>
    Empty,

    /// <summary>Сырой base64 DPAPI CurrentUser (формат 1.1, без метки).</summary>
    LegacyDpapi,

    /// <summary>Машинный ключ, AES-256-GCM, блоб «EBKS» (формат 1.2).</summary>
    MachineKeyV1,

    /// <summary>Не base64 / битый / метка есть, но структура или версия не та (в т.ч. конфиг новее этой версии).</summary>
    Unknown,
}

/// <summary>
/// Чистое (без крипты, без Windows) определение схемы сохранённого секрета по его строковому
/// представлению. Намеренно тестируется без прав/ОС. Важное свойство: метка-есть-но-структура-не-та
/// и неизвестная версия дают <see cref="SecretScheme.Unknown"/> — НИКОГДА не «тихий откат» на легаси
/// (это убивает downgrade-оракул).
/// </summary>
public static class SecretSchemeDetector
{
    /// <summary>Сигнатура блоба машинного ключа: ASCII «EBKS» (в одной семье с EBKE/EBKK).</summary>
    public static readonly byte[] Magic = "EBKS"u8.ToArray();

    /// <summary>Байт схемы для <see cref="SecretScheme.MachineKeyV1"/>.</summary>
    public const byte SchemeMachineKeyV1 = 0x01;

    /// <summary>Версия формата блоба EBKS, понятная этой сборке.</summary>
    public const byte FormatVersion = 1;

    // Смещения полей: [magic(4)][scheme(1)][ver(1)][keyId(16)][aadLen(u16 BE)][aad][salt(16)][nonce(12)][tag(16)][ct]
    private const int HeaderToAadLen = 4 + 1 + 1 + 16 + 2; // 24 — до aadLen включительно
    private const int SaltNonceTag = 16 + 12 + 16;          // фиксированный хвост после aad (ct может быть пустым)

    public static SecretScheme Detect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return SecretScheme.Empty;

        if (!TryDecodeBase64(value, out var bytes))
            return SecretScheme.Unknown; // не base64 → испорчено

        if (!StartsWithMagic(bytes))
            return SecretScheme.LegacyDpapi; // base64, но не наша метка → легаси DPAPI-блоб

        return IsWellFormedMachineKey(bytes)
            ? SecretScheme.MachineKeyV1
            : SecretScheme.Unknown; // метка есть, но структура/версия не та → испорчено / конфиг новее
    }

    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        var buffer = new byte[value.Length]; // декод base64 всегда короче строки — буфера хватает
        if (Convert.TryFromBase64String(value, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }
        bytes = [];
        return false;
    }

    private static bool StartsWithMagic(byte[] b)
    {
        if (b.Length < Magic.Length) return false;
        for (var i = 0; i < Magic.Length; i++)
            if (b[i] != Magic[i]) return false;
        return true;
    }

    private static bool IsWellFormedMachineKey(byte[] b)
    {
        if (b.Length < HeaderToAadLen) return false;
        if (b[4] != SchemeMachineKeyV1) return false; // неизвестная схема → Unknown
        if (b[5] != FormatVersion) return false;       // неизвестная версия (конфиг новее) → Unknown
        int aadLen = (b[22] << 8) | b[23];             // u16 big-endian
        long min = (long)HeaderToAadLen + aadLen + SaltNonceTag; // ct допускаем пустым
        return b.Length >= min;
    }
}
