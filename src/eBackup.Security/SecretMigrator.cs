using System.Security.Cryptography;
using eBackup.Core.Security;

namespace eBackup.Security;

/// <summary>Итог перешифровки одного поля секрета.</summary>
public enum SecretMigrationOutcome
{
    /// <summary>Уже машинный ключ или пусто — трогать не нужно.</summary>
    NotNeeded,

    /// <summary>Перешифровано в машинный ключ (значение в <see cref="SecretMigrationResult.Value"/>).</summary>
    Migrated,

    /// <summary>Легаси-секрет не расшифровался (другой пользователь/профиль) — нужен повторный ввод; легаси НЕ тронут.</summary>
    NeedsReentry,
}

/// <summary>Результат перешифровки: исход + (возможно новое) значение поля.</summary>
public readonly record struct SecretMigrationResult(SecretMigrationOutcome Outcome, string? Value);

/// <summary>
/// Переиспользуемое ядро миграции секретов 1.1→1.2 для ОДНОГО поля, без потери данных.
/// Файловая оркестрация (обход storages.json/schedules.json, .bak, атомарная запись, гейт
/// «только интерактивный исходный пользователь») — отдельный шаг активации (S7); здесь —
/// чистая, тестируемая логика перешифровки одного значения.
/// </summary>
public static class SecretMigrator
{
    public static SecretMigrationResult MigrateField(
        string? value, ISecretProtector legacy, MachineKeyProtector machine, string aad)
    {
        switch (SecretSchemeDetector.Detect(value))
        {
            case SecretScheme.Empty:
            case SecretScheme.MachineKeyV1:
                return new SecretMigrationResult(SecretMigrationOutcome.NotNeeded, value);
            case SecretScheme.LegacyDpapi:
                break;
            default:
                throw new InvalidDataException("Поле секрета повреждено — миграция прервана.");
        }

        string plain;
        try
        {
            plain = legacy.Unprotect(value!);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Расшифровать легаси не удалось (например, профиль другого пользователя) —
            // НЕ перезаписываем и НЕ теряем исходный блоб, просим ввести заново.
            return new SecretMigrationResult(SecretMigrationOutcome.NeedsReentry, value);
        }

        var migrated = machine.Protect(plain, aad);
        if (machine.Unprotect(migrated, aad) != plain) // round-trip verify ДО фиксации
            throw new InvalidOperationException("Проверка перешифровки секрета не прошла — поле не мигрировано.");

        return new SecretMigrationResult(SecretMigrationOutcome.Migrated, migrated);
    }
}
