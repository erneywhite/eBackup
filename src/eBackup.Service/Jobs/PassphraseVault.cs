using System.Security.Cryptography;

namespace eBackup.Service.Jobs;

/// <summary>
/// Сейф разовых парольных фраз для интерактивного шифрования. Клиент один раз присылает фразу
/// (StashPassphrase) — служба выдаёт НЕугадываемый тикет; StartBackup/StartRestore затем обменивают
/// тикет на фразу. Свойства безопасности: id из CSPRNG; single-use (на Consume запись удаляется
/// безусловно); привязка к OwnerSid; короткий TTL; строго синхронный Consume под локом
/// (анти-replay/TOCTOU/гонка — ровно один Consume выигрывает); зануление байтов при удалении и при
/// остановке службы. Plaintext держим в byte[] (не string) и зануляем — managed-string копия неизбежна
/// лишь на границе IPC/движка (принятый остаточный риск).
/// </summary>
public sealed class PassphraseVault : IDisposable
{
    public enum TicketResult { Ok, NotFound, Expired, WrongOwner }

    private sealed class Entry
    {
        public required byte[] Plaintext { get; init; }
        public required string OwnerSid { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }

    private const int MaxLiveTickets = 256; // защита от разрастания (DoS)
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Timer _sweeper;
    private bool _disposed;

    public PassphraseVault()
        => _sweeper = new Timer(_ => SweepExpired(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

    /// <summary>
    /// Положить фразу, получить тикет. <paramref name="plaintext"/> КОПИРУЕТСЯ внутрь — вызывающий
    /// должен занулить свой буфер сам. <paramref name="now"/> — для детерминированных тестов TTL.
    /// </summary>
    public string Issue(byte[] plaintext, string ownerSid, TimeSpan ttl, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var copy = (byte[])plaintext.Clone();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); // 256-битный неугадываемый id
        lock (_gate)
        {
            if (_disposed) { CryptographicOperations.ZeroMemory(copy); throw new ObjectDisposedException(nameof(PassphraseVault)); }
            PurgeExpiredLocked(now);
            if (_entries.Count >= MaxLiveTickets)
            {
                CryptographicOperations.ZeroMemory(copy);
                throw new InvalidOperationException("Слишком много активных тикетов парольных фраз.");
            }
            _entries[id] = new Entry { Plaintext = copy, OwnerSid = ownerSid, ExpiresAt = now + ttl };
        }
        return id;
    }

    /// <summary>
    /// Обменять тикет на фразу (single-use). Возвращает КОПИЮ байтов — вызывающий зануляет её сам.
    /// Найденная запись удаляется в ЛЮБОМ случае (даже при протухании/чужом SID) и её оригинал зануляется.
    /// </summary>
    public (TicketResult Result, byte[]? Plaintext) Consume(string ticket, string ownerSid, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(ticket)) return (TicketResult.NotFound, null);
        lock (_gate)
        {
            if (!_entries.TryGetValue(ticket, out var e))
                return (TicketResult.NotFound, null);
            _entries.Remove(ticket); // single-use: снимаем безусловно
            try
            {
                if (now >= e.ExpiresAt) return (TicketResult.Expired, null);
                if (!string.Equals(e.OwnerSid, ownerSid, StringComparison.Ordinal)) return (TicketResult.WrongOwner, null);
                return (TicketResult.Ok, (byte[])e.Plaintext.Clone());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(e.Plaintext); // оригинал затираем сразу
            }
        }
    }

    private void SweepExpired()
    {
        try { lock (_gate) { if (!_disposed) PurgeExpiredLocked(DateTimeOffset.Now); } }
        catch { /* фоновая чистка не должна падать */ }
    }

    private void PurgeExpiredLocked(DateTimeOffset now)
    {
        if (_entries.Count == 0) return;
        var dead = _entries.Where(kv => now >= kv.Value.ExpiresAt).Select(kv => kv.Key).ToList();
        foreach (var k in dead)
        {
            CryptographicOperations.ZeroMemory(_entries[k].Plaintext);
            _entries.Remove(k);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var e in _entries.Values) CryptographicOperations.ZeroMemory(e.Plaintext);
            _entries.Clear();
        }
        _sweeper.Dispose();
    }
}
