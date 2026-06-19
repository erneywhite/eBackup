using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using eBackup.Platform;

namespace eBackup.Security;

/// <summary>
/// Владелец машинного мастер-ключа (%ProgramData%\eBackup\keys\machine.key). Ключ — 32 байта,
/// лежит в самоописывающемся контейнере «EBKK» (DPAPI-LocalMachine-обёртка + SHA-256 хвост),
/// чтобы служба под SYSTEM могла расшифровать секреты без вошедшего пользователя.
///
/// SHA-256 хвост отличает «файл повреждён» от «ключ машины сменился» (DPAPI не расшифровал),
/// чтобы EnsureKey НИКОГДА не регенерировал поверх существующего-но-нерасшифровываемого ключа.
/// В службе путь ЖИВОЙ: под SYSTEM ключ само-провижинится при первом старте (EnsureKey создаёт его
/// на реальном %ProgramData% и ставит строгий DACL) и используется для всех машинных секретов.
/// </summary>
public sealed class MachineKeyStore
{
    private static readonly byte[] ContainerMagic = "EBKK"u8.ToArray();
    private const byte ContainerVersion = 1;
    private const byte WrapDpapiLocalMachine = 1;
    private static readonly byte[] DpapiEntropy = "eBackup.key.v1"u8.ToArray();
    private const int KeyLength = 32;
    private const int HashLength = 32;
    private const int HeaderLength = 4 + 1 + 1 + 16 + 4; // magic+ver+wrap+keyId+wrappedLen

    private readonly string _keyFile;
    private readonly string _keyDir;
    private readonly bool _hardenAcl;
    private readonly object _lock = new();
    private byte[]? _cachedKey;
    private Guid _cachedKeyId;

    public MachineKeyStore(string? keyFilePath = null)
    {
        _keyFile = keyFilePath ?? AppPaths.MachineKeyFile;
        _keyDir = Path.GetDirectoryName(_keyFile)!;
        _hardenAcl = keyFilePath is null; // строгий DACL — только на реальном %ProgramData%, не на тестовых temp-путях
    }

    /// <summary>Идентификатор активного ключа (создаёт ключ при первом обращении).</summary>
    public Guid ActiveKeyId
    {
        get { EnsureLoaded(); return _cachedKeyId; }
    }

    /// <summary>Получить (создав при необходимости) активный мастер-ключ. Возвращает КОПИЮ — зануляйте после использования.</summary>
    public byte[] EnsureKey()
    {
        EnsureLoaded();
        return (byte[])_cachedKey!.Clone();
    }

    /// <summary>Мастер-ключ по keyId — для расшифровки. Сейчас в кольце один активный ключ (ротация — задел на будущее).</summary>
    public byte[] GetKeyById(Guid keyId)
    {
        EnsureLoaded();
        if (keyId == _cachedKeyId)
            return (byte[])_cachedKey!.Clone();
        throw new MachineKeyUnavailableException(
            $"Ключ {keyId} недоступен на этой машине (секрет создан другой установкой).");
    }

    private void EnsureLoaded()
    {
        if (_cachedKey is not null) return;
        lock (_lock)
        {
            if (_cachedKey is not null) return;
            (_cachedKeyId, _cachedKey) = File.Exists(_keyFile)
                ? LoadContainer(_keyFile)
                : CreateAndPublish();
        }
    }

    private (Guid, byte[]) CreateAndPublish()
    {
        Directory.CreateDirectory(_keyDir);
        if (_hardenAcl) TryApplyStrictAcl(_keyDir, isDirectory: true);

        var keyId = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        var temp = _keyFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var container = BuildContainer(keyId, key);
            // Пишем в уникальный temp, затем атомарно публикуем через File.Move (no-overwrite).
            // Так финальный файл всегда появляется целиком и не залочен — проигравший гонку
            // прочитает его без конфликта доступа (в отличие от прямого CreateNew + FileShare.None).
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(container, 0, container.Length);
                fs.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temp, _keyFile, overwrite: false);
                if (_hardenAcl) TryApplyStrictAcl(_keyFile, isDirectory: false);
            }
            catch (IOException)
            {
                // Гонку проиграли — ключ уже опубликован другим процессом/потоком. Наш temp не нужен.
            }
            // Канарейка + единый путь: читаем ключ победителя из опубликованного файла.
            return LoadContainer(_keyFile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static byte[] BuildContainer(Guid keyId, byte[] key)
    {
        var wrapped = ProtectedData.Protect(key, DpapiEntropy, DataProtectionScope.LocalMachine);
        var body = new byte[HeaderLength + wrapped.Length];
        var o = 0;
        ContainerMagic.CopyTo(body, o); o += 4;
        body[o++] = ContainerVersion;
        body[o++] = WrapDpapiLocalMachine;
        keyId.ToByteArray().CopyTo(body, o); o += 16;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(o, 4), wrapped.Length); o += 4;
        wrapped.CopyTo(body, o);

        var hash = SHA256.HashData(body);
        var result = new byte[body.Length + hash.Length];
        body.CopyTo(result, 0);
        hash.CopyTo(result, body.Length);
        return result;
    }

    private static (Guid, byte[]) LoadContainer(string path)
    {
        var all = File.ReadAllBytes(path);
        if (all.Length < HeaderLength + HashLength)
            throw new MachineKeyCorruptedException("Файл ключа слишком короткий или повреждён.");

        var bodyLen = all.Length - HashLength;
        var expectedHash = SHA256.HashData(all.AsSpan(0, bodyLen));
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, all.AsSpan(bodyLen)))
            throw new MachineKeyCorruptedException("Контрольная сумма файла ключа не совпала — файл повреждён.");

        if (!all.AsSpan(0, 4).SequenceEqual(ContainerMagic))
            throw new MachineKeyCorruptedException("Неверная сигнатура файла ключа.");
        var ver = all[4];
        var wrapMode = all[5];
        if (ver != ContainerVersion || wrapMode != WrapDpapiLocalMachine)
            throw new MachineKeyCorruptedException($"Неподдерживаемый формат файла ключа (ver={ver}, wrap={wrapMode}).");

        var keyId = new Guid(all.AsSpan(6, 16));
        var wrappedLen = BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(22, 4));
        if (wrappedLen < 0 || HeaderLength + (long)wrappedLen != bodyLen)
            throw new MachineKeyCorruptedException("Длина обёрнутого ключа неконсистентна.");
        var wrapped = all.AsSpan(HeaderLength, wrappedLen).ToArray();

        byte[] key;
        try
        {
            key = ProtectedData.Unprotect(wrapped, DpapiEntropy, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            // SHA-256 цел, но DPAPI LocalMachine не расшифровал → ключ машины изменился (НЕ регенерировать!).
            throw new MachineKeyChangedException(
                "Машинный ключ не удалось расшифровать (ключ машины изменился — секреты нужно ввести заново).", ex);
        }

        if (key.Length != KeyLength)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new MachineKeyCorruptedException("Расшифрованный ключ неверной длины.");
        }
        return (keyId, key);
    }

    // Строгий DACL: только SYSTEM и Администраторы, без наследования. Best-effort: в службе под SYSTEM
    // на реальном %ProgramData% применяется при само-провижининге ключа; в тестах (temp-папка, не-админ)
    // строгий ACL/owner может не примениться — это ожидаемо и не ошибка.
    private static void TryApplyStrictAcl(string path, bool isDirectory)
    {
        try
        {
            var sysSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (isDirectory)
            {
                var di = new DirectoryInfo(path);
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                sec.AddAccessRule(new FileSystemAccessRule(sysSid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(sec);
            }
            else
            {
                var fi = new FileInfo(path);
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                sec.AddAccessRule(new FileSystemAccessRule(sysSid, FileSystemRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, AccessControlType.Allow));
                fi.SetAccessControl(sec);
            }
        }
        catch
        {
            // см. комментарий выше — строгий ACL намеренно best-effort (вне службы может не примениться).
        }
    }
}
