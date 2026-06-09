namespace eBackup.Storage.Sftp;

/// <summary>
/// Результат проверки соединения — для кнопки «Тест» в UI перед сохранением параметров.
/// </summary>
public sealed record ConnectionTestResult(bool Success, string Message)
{
    public static ConnectionTestResult Ok(string message) => new(true, message);
    public static ConnectionTestResult Fail(string message) => new(false, message);
}
