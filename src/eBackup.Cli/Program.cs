using System.Text;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage.Sftp;

// Зарегистрированные модули (v1 — только OBS).
IReadOnlyList<IBackupModule> modules = [new ObsBackupModule()];

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "list-modules":
        Console.WriteLine("Доступные модули:");
        foreach (var m in modules)
            Console.WriteLine($"  {m.Id,-10} {m.DisplayName}");
        break;

    case "backup":
    {
        var outDir = GetOption(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "backups");
        var name = GetOption(args, "--name") ?? $"ebackup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var path = await new BackupEngine().CreateBackupAsync(modules, outDir, name);
        Console.WriteLine($"Готово (локально): {path}");

        var sftpId = GetOption(args, "--sftp");
        if (sftpId is not null)
        {
            var provider = await ResolveSftpAsync(sftpId);
            if (provider is null) return 1;
            var remoteName = Path.GetFileName(path);
            Console.WriteLine($"Загружаю на {provider.Name} ...");
            await provider.UploadAsync(path, remoteName);
            Console.WriteLine($"Загружено на сервер: {remoteName}");
        }
        break;
    }

    case "restore":
    {
        var sftpId = GetOption(args, "--sftp");
        var toDir = GetOption(args, "--to");
        var archive = GetOption(args, "--archive");

        if (sftpId is not null)
        {
            var remoteName = GetOption(args, "--name") ?? archive;
            if (remoteName is null)
            {
                Console.Error.WriteLine("Для restore по SFTP укажите --name <имя.ebk> (см. sftp-ls).");
                return 1;
            }
            var provider = await ResolveSftpAsync(sftpId);
            if (provider is null) return 1;
            var tmp = Path.Combine(Path.GetTempPath(), Path.GetFileName(remoteName));
            Console.WriteLine($"Скачиваю {remoteName} с {provider.Name} ...");
            await provider.DownloadAsync(remoteName, tmp);
            archive = tmp;
        }

        if (archive is null)
        {
            Console.Error.WriteLine("Укажите --archive <путь.ebk> либо --sftp <id> --name <имя.ebk>.");
            return 1;
        }

        await new BackupEngine().RestoreAsync(archive, destinationRootOverride: toDir);
        Console.WriteLine(toDir is null
            ? "Восстановление завершено (файлы разложены по местам)."
            : $"Восстановление завершено (извлечено в: {toDir}).");
        break;
    }

    case "sftp-add":
        await SftpAddAsync();
        break;

    case "sftp-list":
        await SftpListAsync();
        break;

    case "sftp-test":
    {
        var id = args.Length > 1 ? args[1] : GetOption(args, "--id");
        if (id is null)
        {
            Console.Error.WriteLine("Укажите id: ebackup sftp-test <id>");
            return 1;
        }
        return await SftpTestAsync(id) ? 0 : 1;
    }

    case "sftp-ls":
    {
        var id = args.Length > 1 ? args[1] : GetOption(args, "--id");
        if (id is null)
        {
            Console.Error.WriteLine("Укажите id: ebackup sftp-ls <id>");
            return 1;
        }
        var provider = await ResolveSftpAsync(id);
        if (provider is null) return 1;
        var files = await provider.ListAsync();
        if (files.Count == 0)
        {
            Console.WriteLine("На сервере нет архивов .ebk в целевой папке.");
        }
        else
        {
            Console.WriteLine("Архивы на сервере:");
            foreach (var f in files)
                Console.WriteLine("  " + f);
        }
        break;
    }

    default:
        Console.WriteLine(
            """
            eBackup CLI (v1, черновик)

            Бэкап / восстановление:
              list-modules                                         Список доступных модулей
              backup  --out <dir> [--name <имя>] [--sftp <id>]     Создать .ebk (+ залить на SFTP)
              restore --archive <путь.ebk> [--to <папка>]          Распаковать (в реальные места или в папку)
              restore --sftp <id> --name <имя.ebk> [--to <папка>]  Скачать с SFTP и распаковать

            SFTP-подключения (учётки шифруются через Windows DPAPI):
              sftp-add                                Добавить/обновить подключение (интерактивно)
              sftp-list                               Список сохранённых подключений
              sftp-test <id>                          Проверить связь
              sftp-ls   <id>                          Список архивов .ebk на сервере
            """);
        break;
}

return 0;

// ---------- общие помощники ----------

static string? GetOption(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static SftpConnectionStore Store() => new(new DpapiSecretProtector());

static string Prompt(string label, string? def = null)
{
    Console.Write(def is null ? $"{label}: " : $"{label} [{def}]: ");
    var input = Console.ReadLine();
    return string.IsNullOrWhiteSpace(input) ? def ?? string.Empty : input.Trim();
}

static string ReadSecret(string label)
{
    Console.Write($"{label}: ");
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            break;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0) sb.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar))
            sb.Append(key.KeyChar);
    }
    return sb.ToString();
}

// ---------- команды SFTP ----------

static async Task SftpAddAsync()
{
    Console.WriteLine("Новое SFTP-подключение (Enter — значение по умолчанию):");

    var id = Prompt("Идентификатор (напр. nas)");
    if (string.IsNullOrWhiteSpace(id))
    {
        Console.Error.WriteLine("Идентификатор обязателен.");
        return;
    }

    var name = Prompt("Имя", id);
    var host = Prompt("Хост/IP");
    var port = int.TryParse(Prompt("Порт", "22"), out var p) ? p : 22;
    var user = Prompt("Логин");

    if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
    {
        Console.Error.WriteLine("Хост и логин обязательны.");
        return;
    }

    string? password = null, keyPath = null, keyPassphrase = null;
    if (Prompt("Вход: [p]ароль или [k]люч?", "p").StartsWith('k'))
    {
        keyPath = Prompt("Путь к приватному ключу");
        var pass = ReadSecret("Парольная фраза ключа (пусто, если нет)");
        keyPassphrase = string.IsNullOrEmpty(pass) ? null : pass;
    }
    else
    {
        password = ReadSecret("Пароль");
    }

    var remoteDir = Prompt("Удалённая папка", ".");

    var options = new SftpConnectionOptions
    {
        Host = host,
        Port = port,
        Username = user,
        Password = password,
        PrivateKeyPath = keyPath,
        PrivateKeyPassphrase = keyPassphrase,
        RemoteDirectory = remoteDir
    };

    var store = Store();
    var saved = store.Protect(id, name, options);
    var all = (await store.LoadAsync()).Where(c => c.Id != id).ToList();
    all.Add(saved);
    await store.SaveAllAsync(all);

    Console.WriteLine($"Сохранено: «{name}» (id: {id}). Файл: {SftpConnectionStore.DefaultFilePath()}");
    Console.WriteLine($"Проверить связь: ebackup sftp-test {id}");
}

static async Task SftpListAsync()
{
    var all = await Store().LoadAsync();
    if (all.Count == 0)
    {
        Console.WriteLine("Сохранённых подключений нет. Добавить: ebackup sftp-add");
        return;
    }

    Console.WriteLine("Сохранённые SFTP-подключения:");
    foreach (var c in all)
    {
        var auth = c.ProtectedPassword is not null ? "пароль"
                 : c.PrivateKeyPath is not null ? "ключ"
                 : "—";
        Console.WriteLine($"  {c.Id,-12} {c.Name,-20} {c.Username}@{c.Host}:{c.Port}  папка={c.RemoteDirectory}  вход={auth}");
    }
}

static async Task<SftpStorageProvider?> ResolveSftpAsync(string id)
{
    var conn = (await Store().LoadAsync()).FirstOrDefault(c => c.Id == id);
    if (conn is null)
    {
        Console.Error.WriteLine($"Подключение с id «{id}» не найдено. Список: ebackup sftp-list");
        return null;
    }
    return new SftpStorageProvider(Store().Unprotect(conn));
}

static async Task<bool> SftpTestAsync(string id)
{
    var provider = await ResolveSftpAsync(id);
    if (provider is null) return false;

    Console.WriteLine($"Проверяю {provider.Name} ...");
    var result = await provider.TestConnectionAsync();
    Console.WriteLine((result.Success ? "OK: " : "ОШИБКА: ") + result.Message);
    return result.Success;
}
