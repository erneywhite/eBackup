using Microsoft.Windows.ApplicationModel.Resources;

namespace eBackup.App;

/// <summary>
/// Локализованные строки для кода (XAML использует x:Uid напрямую). Ключи — из Strings/&lt;lang&gt;/Resources.resw.
/// Язык выбирается через PrimaryLanguageOverride при старте (см. App). Для строк с подстановками —
/// перегрузка с string.Format (в resw храним шаблоны вида «Восстанавливаю {0} файлов…»).
/// </summary>
public static class Loc
{
    private static readonly ResourceLoader Rl = new();

    public static string Get(string key) => Rl.GetString(key);

    // args допускает null-элементы (например, опциональные host/порт/имя) — string.Format отдаёт для них пустую строку.
    public static string Get(string key, params object?[] args) => string.Format(Rl.GetString(key), args);
}
