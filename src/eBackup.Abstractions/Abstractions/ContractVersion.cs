namespace eBackup.Core.Abstractions;

/// <summary>
/// Версия контракта плагинов eBackup. Мажор меняется только на ломающих изменениях.
/// Модули объявляют требуемую версию; хост сверяет её ДО инстанцирования.
/// </summary>
public readonly record struct ApiVersion(int Major, int Minor) : IComparable<ApiVersion>
{
    /// <summary>Разбор «major.minor». Не бросает (вход может быть из недоверенного дескриптора).</summary>
    public static bool TryParse(string? s, out ApiVersion v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Split('.');
        if (!int.TryParse(parts[0], out var major)) return false;

        var minor = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minor)) return false;

        v = new ApiVersion(major, minor);
        return true;
    }

    public int CompareTo(ApiVersion other)
        => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>Текущая версия контракта, реализуемая этим хостом.</summary>
public static class ContractInfo
{
    public static readonly ApiVersion Current = new(1, 0);
}
