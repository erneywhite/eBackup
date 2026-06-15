using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using eBackup.Core.Security;

namespace eBackup.Security;

/// <summary>
/// Шифрование секретов машинным ключом: AES-256-GCM, но ключ КАЖДОГО вызова —
/// HKDF-SHA256(masterKey, свежая соль). Поэтому даже случайно совпавший 96-битный nonce
/// не даёт повтора пары (key,nonce) — безопасно при любом объёме секретов и даже на
/// клон-образе VM со «склеившимся» CSPRNG.
///
/// Формат блоба «EBKS» (base64, drop-in вместо прежнего base64 DPAPI):
///   [magic 'EBKS'(4)][scheme(1)=1][ver(1)=1][keyId(16)][aadLen(u16 BE)][aad][salt(16)][nonce(12)][tag(16)][ct]
/// associatedData GCM = весь заголовок до aad включительно — так magic/scheme/ver/keyId/aad
/// защищены от подмены. AAD привязывает блоб к слоту: при расшифровке сохранённый aad обязан
/// совпасть с ожидаемым (анти-swap), и заголовок (с этим aad) скармливается в GCM.
/// </summary>
public sealed class MachineKeyProtector : ISecretProtector, ISecretProtectorWithAad
{
    private static readonly byte[] Magic = SecretSchemeDetector.Magic;
    private const byte Scheme = SecretSchemeDetector.SchemeMachineKeyV1;
    private const byte Version = SecretSchemeDetector.FormatVersion;
    private static readonly byte[] HkdfInfo = "eBackup.secret.v1"u8.ToArray();

    private const int KeyIdLen = 16, SaltLen = 16, NonceLen = 12, TagLen = 16, SubKeyLen = 32;
    private const int FixedHeader = 4 + 1 + 1 + KeyIdLen + 2; // до aadLen включительно = 24

    private readonly MachineKeyStore _store;

    public MachineKeyProtector(MachineKeyStore store) => _store = store;

    // ISecretProtector — без AAD (пустой aad). Удобно для путей, где привязка к слоту не нужна.
    public string Protect(string plaintext) => Protect(plaintext, string.Empty);
    public string Unprotect(string protectedValue) => Unprotect(protectedValue, string.Empty);

    public string Protect(string plaintext, string aad)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext; // пусто остаётся пусто — блоба нет

        var aadBytes = Encoding.UTF8.GetBytes(aad ?? string.Empty);
        if (aadBytes.Length > ushort.MaxValue)
            throw new ArgumentException("AAD слишком длинный.", nameof(aad));

        var keyId = _store.ActiveKeyId;
        var master = _store.EnsureKey();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltLen);
            var nonce = RandomNumberGenerator.GetBytes(NonceLen);

            var headerEnd = FixedHeader + aadBytes.Length;
            var blob = new byte[headerEnd + SaltLen + NonceLen + TagLen + plainBytes.Length];
            var o = 0;
            Magic.CopyTo(blob, o); o += 4;
            blob[o++] = Scheme;
            blob[o++] = Version;
            keyId.ToByteArray().CopyTo(blob, o); o += KeyIdLen;
            BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(o, 2), (ushort)aadBytes.Length); o += 2;
            aadBytes.CopyTo(blob, o); o += aadBytes.Length; // o == headerEnd
            salt.CopyTo(blob, o); o += SaltLen;
            nonce.CopyTo(blob, o); o += NonceLen;
            var tagOffset = o; o += TagLen;
            var ctOffset = o;

            var subKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, SubKeyLen, salt, HkdfInfo);
            try
            {
                using var gcm = new AesGcm(subKey, TagLen);
                gcm.Encrypt(nonce, plainBytes,
                    blob.AsSpan(ctOffset, plainBytes.Length),
                    blob.AsSpan(tagOffset, TagLen),
                    blob.AsSpan(0, headerEnd)); // AAD = заголовок целиком
            }
            finally
            {
                CryptographicOperations.ZeroMemory(subKey);
            }

            return Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public string Unprotect(string protectedValue, string expectedAad)
    {
        if (string.IsNullOrEmpty(protectedValue)) return protectedValue;

        byte[] blob;
        try { blob = Convert.FromBase64String(protectedValue); }
        catch (FormatException) { throw new InvalidDataException("Секрет не является корректным блобом."); }

        if (blob.Length < FixedHeader
            || !blob.AsSpan(0, 4).SequenceEqual(Magic)
            || blob[4] != Scheme || blob[5] != Version)
            throw new InvalidDataException("Секрет другой версии или повреждён.");

        var keyId = new Guid(blob.AsSpan(6, KeyIdLen));
        int aadLen = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(6 + KeyIdLen, 2));
        var headerEnd = FixedHeader + aadLen;
        if (blob.Length < (long)headerEnd + SaltLen + NonceLen + TagLen)
            throw new InvalidDataException("Секрет повреждён.");

        // Анти-swap: сохранённый aad обязан совпасть с ожидаемым слотом.
        var expectedBytes = Encoding.UTF8.GetBytes(expectedAad ?? string.Empty);
        if (!blob.AsSpan(FixedHeader, aadLen).SequenceEqual(expectedBytes))
            throw new InvalidDataException("Секрет не соответствует своему слоту (AAD).");

        var master = _store.GetKeyById(keyId); // нет ключа → MachineKeyUnavailableException (типизировано)
        try
        {
            var saltOff = headerEnd;
            var nonceOff = saltOff + SaltLen;
            var tagOff = nonceOff + NonceLen;
            var ctOff = tagOff + TagLen;
            var ctLen = blob.Length - ctOff;

            var subKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, SubKeyLen,
                blob.AsSpan(saltOff, SaltLen).ToArray(), HkdfInfo);
            var plain = new byte[ctLen];
            try
            {
                using var gcm = new AesGcm(subKey, TagLen);
                gcm.Decrypt(blob.AsSpan(nonceOff, NonceLen),
                    blob.AsSpan(ctOff, ctLen),
                    blob.AsSpan(tagOff, TagLen),
                    plain,
                    blob.AsSpan(0, headerEnd));
                return Encoding.UTF8.GetString(plain);
            }
            catch (AuthenticationTagMismatchException)
            {
                // Единая ошибка на любой провал аутентификации — без оракула формата/длины.
                throw new InvalidDataException("Секрет повреждён или зашифрован другим ключом.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(subKey);
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }
}
