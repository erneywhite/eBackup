namespace eBackup.Core.Security;

/// <summary>
/// Append-only компаньон к <see cref="ISecretProtector"/>: шифрование с дополнительными
/// аутентифицируемыми данными (AAD), которые привязывают зашифрованный блоб к его «слоту»
/// (например, хранилище + поле). Это не даёт подменить пароль одного хранилища блобом из
/// другого. Замороженный <see cref="ISecretProtector"/> не меняем — добавляем рядом.
/// </summary>
public interface ISecretProtectorWithAad
{
    /// <summary>Зашифровать строку, аутентифицируя вместе с ней <paramref name="aad"/> (в шифр не попадает).</summary>
    string Protect(string plaintext, string aad);

    /// <summary>
    /// Расшифровать значение из <see cref="Protect(string,string)"/>. <paramref name="expectedAad"/>
    /// должен совпасть с тем, что был при шифровании, иначе — ошибка (защита от подмены слота).
    /// </summary>
    string Unprotect(string protectedValue, string expectedAad);
}
