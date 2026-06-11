using eBackup.Core.Abstractions;
using Renci.SshNet;

namespace eBackup.Storage.Sftp;

/// <summary>
/// Хранение архивов по SFTP (на базе SSH.NET).
///
/// SSH.NET — синхронная библиотека, поэтому сетевые операции выполняются в
/// <see cref="Task.Run(Action, CancellationToken)"/>, чтобы не блокировать вызывающий
/// поток (в т.ч. UI). На каждую операцию открывается отдельное соединение —
/// просто и надёжно для нечастых операций бэкапа; пул соединений добавим при необходимости.
/// </summary>
public sealed class SftpStorageProvider : IStorageProvider
{
    private readonly SftpConnectionOptions _options;

    public SftpStorageProvider(SftpConnectionOptions options) => _options = options;

    public string Name => $"SFTP ({_options.Username}@{_options.Host}:{_options.Port})";

    public Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var client = Connect();
            EnsureRemoteDirectory(client, _options.RemoteDirectory);
            using var stream = File.OpenRead(localFilePath);
            client.UploadFile(stream, CombineRemote(_options.RemoteDirectory, remoteName), canOverride: true);
        }, ct);

    public Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var client = Connect();
            var dir = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            using var stream = File.Create(localFilePath);
            client.DownloadFile(CombineRemote(_options.RemoteDirectory, remoteName), stream);
        }, ct);

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<string>>(() =>
        {
            using var client = Connect();
            if (!client.Exists(_options.RemoteDirectory))
                return [];

            return client.ListDirectory(_options.RemoteDirectory)
                .Where(f => !f.IsDirectory && f.Name.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Name)
                .ToList();
        }, ct);

    /// <summary>Удалить архив на сервере.</summary>
    public Task DeleteAsync(string remoteName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var client = Connect();
            client.DeleteFile(CombineRemote(_options.RemoteDirectory, remoteName));
        }, ct);

    /// <summary>
    /// Детальный список архивов (имя, размер, дата изменения). Дополнительных запросов
    /// не делает: атрибуты приходят вместе с обычным листингом каталога.
    /// </summary>
    public Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<RemoteFileInfo>>(() =>
        {
            using var client = Connect();
            if (!client.Exists(_options.RemoteDirectory))
                return [];

            return client.ListDirectory(_options.RemoteDirectory)
                .Where(f => !f.IsDirectory && f.Name.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase))
                .Select(f => new RemoteFileInfo(f.Name, f.Length, f.LastWriteTime))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }, ct);

    /// <summary>
    /// Список подкаталогов указанной удалённой папки — для ленивого построения дерева
    /// выбора каталога в UI (вместо ручного ввода пути).
    /// </summary>
    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string remotePath = ".", CancellationToken ct = default)
        => Task.Run<IReadOnlyList<string>>(() =>
        {
            using var client = Connect();
            return client.ListDirectory(remotePath)
                .Where(f => f.IsDirectory && f.Name is not "." and not "..")
                .Select(f => f.FullName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    /// <summary>
    /// Проверка соединения и аутентификации — для кнопки «Тест» перед сохранением параметров.
    /// Никогда не бросает: ошибку возвращает в <see cref="ConnectionTestResult.Message"/>.
    /// </summary>
    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                using var client = Connect();
                return ConnectionTestResult.Ok($"Подключение успешно. Рабочая папка: {client.WorkingDirectory}");
            }
            catch (Exception ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }, ct);

    /// <summary>
    /// Открыть удалённый файл как поток с Seek: читаются только нужные куски
    /// (например, оглавление ZIP). Соединение живёт, пока открыт поток.
    /// </summary>
    public Task<Stream> OpenSeekableReadAsync(string remoteName, CancellationToken ct = default)
        => Task.Run<Stream>(() =>
        {
            var client = new SftpClient(BuildConnectionInfo(_options));
            try
            {
                client.Connect();
                var stream = client.OpenRead(CombineRemote(_options.RemoteDirectory, remoteName));
                return new OwnedStream(stream, client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }, ct);

    private SftpClient Connect()
    {
        var client = new SftpClient(BuildConnectionInfo(_options));
        client.Connect();
        return client;
    }

    private static ConnectionInfo BuildConnectionInfo(SftpConnectionOptions o)
    {
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrEmpty(o.PrivateKeyPem))
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(o.PrivateKeyPem));
            var keyFile = string.IsNullOrEmpty(o.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, o.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(o.Username, keyFile));
        }

        if (!string.IsNullOrEmpty(o.Password))
            methods.Add(new PasswordAuthenticationMethod(o.Username, o.Password));

        if (methods.Count == 0)
            throw new InvalidOperationException("Для SFTP нужно указать пароль или приватный ключ.");

        return new ConnectionInfo(o.Host, o.Port, o.Username, methods.ToArray());
    }

    private static void EnsureRemoteDirectory(SftpClient client, string remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir) || remoteDir == ".")
            return;

        var segments = remoteDir.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = remoteDir.StartsWith('/') ? string.Empty : ".";
        foreach (var seg in segments)
        {
            path = path.Length == 0 ? "/" + seg : path + "/" + seg;
            if (!client.Exists(path))
                client.CreateDirectory(path);
        }
    }

    private static string CombineRemote(string dir, string name)
        => string.IsNullOrEmpty(dir) || dir == "."
            ? name
            : dir.TrimEnd('/') + "/" + name;
}
