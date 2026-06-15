namespace eBackup.Core.Security;

/// <summary>Тип объекта, которому принадлежит секрет (часть AAD).</summary>
public enum SecretScope
{
    Storage,
    Schedule,
}

/// <summary>Какое именно поле секрета (часть AAD). Фиксированный список — чтобы AAD был канонический.</summary>
public enum SecretField
{
    Password,
    RefreshToken,
    AccessToken,
    Passphrase,
    Generic,
}

/// <summary>
/// Единый канонический построитель AAD для секретов: привязывает блоб к его «слоту»
/// (тип/идентификатор/поле). ОДИН и тот же билдер используется и при миграции, и в рантайме —
/// иначе анти-swap-гарантия сломается из-за рассинхрона строк.
/// </summary>
public static class SecretAad
{
    public static string For(SecretScope scope, string id, SecretField field)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("id обязателен для AAD секрета.", nameof(id));
        return $"{scope}/{id}/{field}";
    }
}
