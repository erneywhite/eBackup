using System.Text;
using eBackup.Core.Abstractions;
using eBackup.Core.Crypto;
using eBackup.Core.Engine;
using eBackup.Core.Modules;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage.Sftp;

// Реестр модулей: встроенные (OBS, приоритетнее) + декларативные drop-in + (позже) DLL.
var registry = new ModuleRegistry(
[
    new BuiltInModuleSource([new ObsBackupModule()]),
    new DeclarativeModuleSource(),
]);
IReadOnlyList<IBackupModule> modules = registry.LoadEnabled();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "list-modules":
        Console.WriteLine("Модули:");
        foreach (var d in registry.Discover())
        {
            var status = d.Problem is not null ? $"ЗАБЛОКИРОВАН ({d.Problem})"
                       : d.Enabled ? "включён"
                       : "выключен";
            Console.WriteLine($"  {d.Id,-12} {d.DisplayName,-18} [{d.Source}] {status}");
        }
        break;

    case "backup":
    {
        var outDir = GetOption(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "backups");

        // --modules obs,foo — ограничить набор; по умолчанию все включённые.
        var selectedIds = GetOption(args, "--modules");
        var chosen = selectedIds is null
            ? modules
            : modules.Where(m => selectedIds
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(m.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();

        var name = GetOption(args, "--name") ?? BackupNaming.DefaultName(chosen);

        string? passphrase = null;
        if (args.Contains("--encrypt"))
        {
            // Неинтерактивно (скрипты/расписание) — через переменную окружения.
            passphrase = Environment.GetEnvironmentVariable("EBACKUP_PASSPHRASE");
            if (string.IsNullOrEmpty(passphrase))
                passphrase = ReadNewPassphrase();
            if (string.IsNullOrEmpty(passphrase)) return 1;
        }

        var path = await new BackupEngine().CreateBackupAsync(chosen, outDir, name, passphrase, new ConsoleProgress());
        Console.WriteLine(passphrase is null
            ? $"Готово (локально): {path}"
            : $"Готово (локально, зашифровано): {path}");

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

        var assetsDir = GetOption(args, "--assets-dir")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Assets");

        // Режим разрешения конфликтов: replace (с .bak) / overwrite / add-missing.
        var conflict = (GetOption(args, "--conflict") ?? "backup").ToLowerInvariant() switch
        {
            "overwrite" => ConflictPolicy.Overwrite,
            "skip" or "add" or "add-missing" => ConflictPolicy.Skip,
            _ => ConflictPolicy.BackupExisting
        };

        // Зашифрованный архив определяем сами и спрашиваем парольную фразу.
        string? restorePass = null;
        if (ArchiveCipher.IsEncrypted(archive))
        {
            restorePass = Environment.GetEnvironmentVariable("EBACKUP_PASSPHRASE");
            if (string.IsNullOrEmpty(restorePass))
            {
                Console.WriteLine("Архив зашифрован.");
                restorePass = ReadSecret("Парольная фраза");
            }
            if (string.IsNullOrEmpty(restorePass))
            {
                Console.Error.WriteLine("Пустая фраза — отмена.");
                return 1;
            }
        }

        await new BackupEngine().RestoreAsync(
            archive,
            modules: registry.LoadForRestore(), // выключатель модулей на восстановление не влияет
            conflictPolicy: conflict,
            destinationRootOverride: toDir,
            assetsDirectory: assetsDir,
            passphrase: restorePass,
            progress: new ConsoleProgress());

        Console.WriteLine(toDir is null
            ? $"Восстановление завершено (файлы разложены по местам; ассеты: {assetsDir})."
            : $"Восстановление завершено (извлечено в: {toDir}; ассеты: {assetsDir}).");
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

    case "module-add":
    {
        var file = args.Length > 1 ? args[1] : GetOption(args, "--file");
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine("Укажите путь к *.module.json: ebackup module-add <файл>");
            return 1;
        }
        if (!file.EndsWith(".module.json", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Файл должен оканчиваться на .module.json");
            return 1;
        }
        Directory.CreateDirectory(ModulePaths.ModulesDirectory);
        var dest = Path.Combine(ModulePaths.ModulesDirectory, Path.GetFileName(file));
        File.Copy(file, dest, overwrite: true);
        Console.WriteLine($"Импортирован модуль: {dest}");
        Console.WriteLine("Проверить: ebackup list-modules");
        break;
    }

    default:
        Console.WriteLine(
            """
            eBackup CLI (v1, черновик)

            Бэкап / восстановление:
              list-modules                                         Список доступных модулей
              backup  --out <dir> [--name <имя>] [--modules id,..] [--sftp <id>] [--encrypt]   Создать .ebk
              restore --archive <путь.ebk> [--to <папка>] [--assets-dir <папка>]   Распаковать
              restore --sftp <id> --name <имя.ebk> [--to <папка>]                  Скачать с SFTP и распаковать
                  (--encrypt: AES-256-GCM, ключ из парольной фразы через Argon2id; restore спросит фразу сам)
                  (неинтерактивно фразу можно задать через переменную окружения EBACKUP_PASSPHRASE)
                  (--assets-dir: куда раскладывать внешние ассеты модулей; по умолчанию Documents\eBackup\Assets)
                  (--conflict: backup [по умолч., с .bak] | overwrite | add-missing)
                  (восстановление плагинов в Program Files требует запуска от администратора)

            SFTP-подключения (учётки шифруются через Windows DPAPI):
              sftp-add                                Добавить/обновить подключение (интерактивно)
              sftp-list                               Список сохранённых подключений
              sftp-test <id>                          Проверить связь
              sftp-ls   <id>                          Список архивов .ebk на сервере

            Модули (%APPDATA%\eBackup\modules):
              module-add <файл.module.json>           Импортировать декларативный модуль (drop-in)
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

static string? ReadNewPassphrase()
{
    var first = ReadSecret("Парольная фраза для шифрования");
    if (string.IsNullOrEmpty(first))
    {
        Console.Error.WriteLine("Пустая фраза — отмена.");
        return null;
    }
    if (ReadSecret("Повторите фразу") != first)
    {
        Console.Error.WriteLine("Фразы не совпадают — отмена.");
        return null;
    }
    return first;
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

    string? password = null, keyPem = null, keyPassphrase = null;
    if (Prompt("Вход: [p]ароль или [k]люч?", "p").StartsWith('k'))
    {
        // Содержимое ключа читается один раз и хранится зашифрованным (DPAPI) —
        // сам файл ключа после этого может вообще не лежать на диске.
        var keyFile = Prompt("Путь к файлу приватного ключа (содержимое сохранится зашифрованным)");
        if (!File.Exists(keyFile))
        {
            Console.Error.WriteLine($"Файл не найден: {keyFile}");
            return;
        }
        keyPem = await File.ReadAllTextAsync(keyFile);
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
        PrivateKeyPem = keyPem,
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
                 : c.ProtectedPrivateKey is not null ? "ключ"
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

/// <summary>Прогресс бэкапа в консоль.</summary>
sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine("  " + value);
}
