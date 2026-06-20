namespace eBackup.Storage;

/// <summary>
/// Дешёвое определение «зашифрован ли архив» по голове файла — читаем только первые 4 байта
/// (сигнатура EBKE формата <c>eBackup.Core.Crypto.ArchiveCipher</c>), без полной закачки.
/// </summary>
internal static class ArchiveHead
{
    // Сигнатура зашифрованного архива (.ebk). Дублируется намеренно: eBackup.Storage не тянет крипту,
    // а формат v1 стабилен. Меняется только вместе с ArchiveCipher.Magic.
    private static readonly byte[] Ebke = "EBKE"u8.ToArray();

    /// <summary>Начинается ли поток с сигнатуры EBKE. Читает только 4 байта вперёд (seek не нужен).</summary>
    public static async Task<bool> IsEbkeAsync(Stream forwardOnly, CancellationToken ct = default)
    {
        var head = new byte[4];
        var n = 0;
        while (n < head.Length)
        {
            var r = await forwardOnly.ReadAsync(head.AsMemory(n), ct).ConfigureAwait(false);
            if (r == 0) break;
            n += r;
        }
        return n == head.Length && head.AsSpan().SequenceEqual(Ebke);
    }
}
