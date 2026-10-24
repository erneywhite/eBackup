using eBackup.Core.Abstractions;
using eBackup.Core.Paths;
using eBackup.Platform;

namespace eBackup.Service.Security;

/// <summary>
/// Куда служба (SYSTEM) может писать при восстановлении от имени пользователя:
/// <list type="number">
/// <item>Собственные папки eBackup (ProgramData\eBackup — ключи/конфиг; папка установки службы) — НИКОГДА:
///   иначе архив мог бы подменить конфиг или код самой службы.</item>
/// <item>{PROGRAMFILES}/{PROGRAMDATA} — как раньше, без проверки прав, но ТОЛЬКО в пути, которые
///   объявляет установленный модуль с тем же id (напр. плагины OBS в Program Files). Путь из архива
///   вне объявленных модулем — на общих основаниях (п. 3).</item>
/// <item>Всё остальное (профиль, свои папки, «восстановить в папку») — только если пользователь сам
///   имеет право туда писать (<see cref="IPathAccess"/>).</item>
/// </list>
/// </summary>
public sealed class RestoreWritePolicy
{
    private static readonly string[] MachineTokens = ["{PROGRAMFILES}", "{PROGRAMDATA}"];

    private readonly IPathAccess _access;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _declaredMachinePaths;
    private readonly IReadOnlyList<string> _protectedRoots;

    public RestoreWritePolicy(
        IPathAccess access,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declaredMachinePaths,
        IReadOnlyList<string>? protectedRoots = null)
    {
        _access = access;
        _declaredMachinePaths = declaredMachinePaths;
        _protectedRoots = protectedRoots ?? DefaultProtectedRoots();
    }

    /// <summary>Папки, куда восстановление не пишет никогда: данные/конфиг eBackup и папка установки службы.</summary>
    public static IReadOnlyList<string> DefaultProtectedRoots()
        => [AppPaths.ProgramData, AppContext.BaseDirectory];

    /// <summary>
    /// Пути в {PROGRAMFILES}/{PROGRAMDATA}, которые объявляют установленные модули (по id). Модуль сам
    /// (по своему DiscoverAsync) говорит, что он туда пишет; архив этот список расширить не может.
    /// Сбой обнаружения модуля — просто нет исключений для него (сработает обычная проверка прав).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> CollectDeclaredMachinePathsAsync(
        IEnumerable<IBackupModule> modules, Func<string, string> resolveToken, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            try
            {
                if (module is IUserScopedDiscovery scoped)
                    scoped.UseSourceResolver(resolveToken);
                var entries = await module.DiscoverAsync(ct).ConfigureAwait(false);
                var machine = entries
                    .Select(e => Normalize(e.TokenPath))
                    .Where(IsMachineToken)
                    .ToList();
                if (machine.Count > 0)
                    result[module.Id] = machine;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* модуль не смог обнаружить пути — без исключений для него */ }
        }
        return result;
    }

    /// <summary>
    /// Восстановление «в исходные места»: можно ли записать <paramref name="destination"/>
    /// (запись <paramref name="tokenPath"/> модуля <paramref name="moduleId"/>).
    /// </summary>
    public bool CanWrite(string moduleId, string tokenPath, string destination)
    {
        if (IsProtected(destination))
            return false;

        var token = Normalize(tokenPath);
        if (IsMachineToken(token)
            && _declaredMachinePaths.TryGetValue(moduleId, out var declared)
            && declared.Any(d => token.Equals(d, StringComparison.OrdinalIgnoreCase)
                              || token.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase)))
            return true;

        return _access.CanWrite(destination);
    }

    /// <summary>
    /// Восстановление «в выбранную папку» (и папка ассетов): исключений модулей нет — только права
    /// пользователя на саму цель (а каждый файл проверяется отдельно: junction внутри папки не поможет).
    /// </summary>
    public bool CanWriteIntoFolder(string destination)
        => !IsProtected(destination) && _access.CanWrite(destination);

    private bool IsProtected(string destination)
        => _protectedRoots.Any(root => !string.IsNullOrEmpty(root) && PathSafety.IsWithin(root, destination));

    private static bool IsMachineToken(string tokenPath)
        => MachineTokens.Any(t => tokenPath.StartsWith(t, StringComparison.Ordinal));

    private static string Normalize(string tokenPath) => tokenPath.Replace('\\', '/').TrimEnd('/');
}
