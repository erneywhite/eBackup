using System.Globalization;
using System.Runtime.CompilerServices;

namespace eBackup.Tests;

/// <summary>
/// Тесты сверяют русские строки журнала/ошибок, а язык back-end по умолчанию — русский (см. L.SetCulture).
/// Фиксируем его для всей тестовой сборки, чтобы результат не зависел от локали машины (CI-раннер — en-US).
/// Тесты, проверяющие английский, по-прежнему переключают CurrentUICulture сами.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Init()
    {
        var ru = CultureInfo.GetCultureInfo("ru");
        CultureInfo.DefaultThreadCurrentUICulture = ru;
        CultureInfo.CurrentUICulture = ru;
    }
}
