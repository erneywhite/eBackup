using eBackup.Core.Security;

namespace eBackup.Security;

/// <summary>
/// Протектор для GUI/CLI: ПИШЕТ всегда машинным ключом (EBKS), а ЧИТАЕТ по схеме —
/// машинные блобы машинным ключом, легаси (сырой DPAPI CurrentUser 1.1) — легаси-протектором
/// (только расшифровка). Неизвестное/битое — явная ошибка, без тихого отката.
///
/// Служба под SYSTEM использует НЕ его, а <see cref="MachineKeyProtector"/> напрямую (без
/// DPAPI-фоллбэка — она не должна уметь читать CurrentUser-секреты). Вшивается в живой путь
/// при активации на S7; сейчас аддитивно/дормантно.
/// </summary>
public sealed class CompositeSecretProtector : ISecretProtector, ISecretProtectorWithAad
{
    private readonly MachineKeyProtector _machine;
    private readonly ISecretProtector _legacy;

    public CompositeSecretProtector(MachineKeyProtector machine, ISecretProtector legacy)
    {
        _machine = machine;
        _legacy = legacy;
    }

    public string Protect(string plaintext) => _machine.Protect(plaintext);

    public string Protect(string plaintext, string aad) => _machine.Protect(plaintext, aad);

    public string Unprotect(string protectedValue) => Unprotect(protectedValue, string.Empty);

    public string Unprotect(string protectedValue, string expectedAad)
        => SecretSchemeDetector.Detect(protectedValue) switch
        {
            SecretScheme.Empty => protectedValue,
            SecretScheme.MachineKeyV1 => _machine.Unprotect(protectedValue, expectedAad),
            SecretScheme.LegacyDpapi => _legacy.Unprotect(protectedValue), // легаси-блобы без AAD
            _ => throw new InvalidDataException("Неизвестный или повреждённый формат секрета."),
        };
}
