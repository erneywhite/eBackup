using System.Text;
using eBackup.Core.Abstractions;
using eBackup.Core.Crypto;
using eBackup.Core.Engine;
using eBackup.Core.Modules;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage;

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
        Console.WriteLine($"Готово (локально): {path}");

        var storageId = GetOption(args, "--storage") ?? GetOption(args, "--sftp");
        if (storageId is not null)
        {
            var storage = await ResolveStorageAsync(storageId);
            if (storage is null) return 1;
            Console.WriteLine($"Сохраняю в «{storage.Name}» ...");
            await storage.UploadAsync(path, Path.GetFileName(path));
            Console.WriteLine($"Сохранено: {Path.GetFileName(path)}");
        }
        break;
    }

    case "restore":
    {
        var storageId = GetOption(args, "--storage") ?? GetOption(args, "--sftp");
        var toDir = GetOption(args, "--to");
        var archive = GetOption(args, "--archive");

        if (storageId is not null)
        {
            var remoteName = GetOption(args, "--name") ?? archive;
            if (remoteName is null)
            {
                Console.Error.WriteLine("Для restore из хранилища укажите --name <имя.ebk> (см. storage-ls).");
                return 1;
            }
            var storage = await ResolveStorageAsync(storageId);
            if (storage is null) return 1;
            var tmp = Path.Combine(Path.GetTempPath(), Path.GetFileName(remoteName));
            Console.WriteLine($"Скачиваю {remoteName} из «{storage.Name}» ...");
            await storage.DownloadAsync(remoteName, tmp);
            archive = tmp;
        }

        if (archive is null)
        {
            Console.Error.WriteLine("Укажите --archive <путь.ebk> либо --storage <id> --name <имя.ebk>.");
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

    case "storage-list":
    case "sftp-list":
        await StorageListAsync();
        break;

    case "storage-test":
    case "sftp-test":
    {
        var id = args.Length > 1 ? args[1] : GetOption(args, "--id");
        if (id is null)
        {
            Console.Error.WriteLine("Укажите id: ebackup storage-test <id>");
            return 1;
        }
        var storage = await ResolveStorageAsync(id);
        if (storage is null) return 1;
        Console.WriteLine($"Проверяю «{storage.Name}» ...");
        var result = await storage.TestAsync();
        Console.WriteLine((result.Success ? "OK: " : "ОШИБКА: ") + result.Message);
        return result.Success ? 0 : 1;
    }

    case "storage-ls":
    case "sftp-ls":
    {
        var id = args.Length > 1 ? args[1] : GetOption(args, "--id");
        if (id is null)
        {
            Console.Error.WriteLine("Укажите id: ebackup storage-ls <id>");
            return 1;
        }
        var storage = await ResolveStorageAsync(id);
        if (storage is null) return 1;
        var files = await storage.ListDetailedAsync();
        if (files.Count == 0)
        {
            Console.WriteLine("Архивов нет.");
        }
        else
        {
            Console.WriteLine($"Архивы в «{storage.Name}»:");
            foreach (var f in files)
                Console.WriteLine($"  {f.Name,-50} {f.Length / 1024.0 / 1024.0,8:0.#} МБ  {f.LastWriteTime:dd.MM.yyyy HH:mm}");
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
            eBackup CLI

            Бэкап / восстановление:
              list-modules                                         Список доступных модулей
              backup  --out <dir> [--name <имя>] [--modules id,..] [--storage <id>] [--encrypt]   Создать .ebk
              restore --archive <путь.ebk> [--to <папка>] [--assets-dir <папка>]   Распаковать
              restore --storage <id> --name <имя.ebk> [--to <папка>]               Скачать из хранилища и распаковать
                  (--encrypt: AES-256-GCM, ключ из парольной фразы через Argon2id; restore спросит фразу сам)
                  (неинтерактивно фразу можно задать через переменную окружения EBACKUP_PASSPHRASE)
                  (--assets-dir: куда раскладывать внешние ассеты модулей; по умолчанию Documents\eBackup\Assets)
                  (--conflict: backup [по умолч., с .bak] | overwrite | add-missing)
                  (восстановление плагинов в Program Files требует запуска от администратора)

            Хранилища (единый конфиг с GUI; секреты шифруются через Windows DPAPI):
              sftp-add                                Добавить/обновить SFTP-хранилище (интерактивно)
              storage-list                            Список хранилищ
              storage-test <id>                       Проверить доступность
              storage-ls   <id>                       Список архивов .ebk в хранилище

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

static StorageStore Store() => new(new DpapiSecretProtector());

static async Task<IArchiveStorage?> ResolveStorageAsync(string id)
{
    var store = Store();
    var saved = (await store.LoadAsync()).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    if (saved is null)
    {
        Console.Error.WriteLine($"Хранилище с id «{id}» не найдено. Список: ebackup storage-list");
        return null;
    }
    return StorageFactory.Create(saved, store.Protector);
}

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

// ---------- команды хранилищ ----------

static async Task SftpAddAsync()
{
    Console.WriteLine("Новое SFTP-хранилище (Enter — значение по умолчанию):");

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

    var store = Store();
    var saved = new SavedStorage
    {
        Id = id,
        Name = name,
        Kind = StorageKind.Sftp,
        Host = host,
        Port = port,
        Username = user,
        ProtectedPassword = password is null ? null : store.Protect(password),
        ProtectedPrivateKey = keyPem is null ? null : store.Protect(keyPem),
        ProtectedKeyPassphrase = keyPassphrase is null ? null : store.Protect(keyPassphrase),
        RemoteDirectory = remoteDir
    };

    var all = (await store.LoadAsync()).Where(s => s.Id != id).Append(saved).ToList();
    await store.SaveAllAsync(all);

    Console.WriteLine($"Сохранено: «{name}» (id: {id}). Файл: {StorageStore.DefaultFilePath()}");
    Console.WriteLine($"Проверить: ebackup storage-test {id}");
}

static async Task StorageListAsync()
{
    var all = await Store().LoadAsync();
    if (all.Count == 0)
    {
        Console.WriteLine("Хранилищ нет. Добавить SFTP: ebackup sftp-add (папки — в GUI).");
        return;
    }

    Console.WriteLine("Хранилища:");
    foreach (var s in all)
    {
        var details = s.Kind switch
        {
            StorageKind.LocalFolder => s.Path,
            StorageKind.Sftp => $"{s.Username}@{s.Host}:{s.Port}  папка={s.RemoteDirectory}",
            StorageKind.Ftp => $"{(s.UseFtps ? "ftps" : "ftp")}://{s.Username}@{s.Host}:{s.Port}  папка={s.RemoteDirectory}",
            StorageKind.S3 => $"s3 {s.ServiceUrl}  бакет={s.Bucket}  префикс={s.RemoteDirectory}",
            _ => ""
        };
        Console.WriteLine($"  {s.Id,-12} {s.Name,-20} [{s.Kind}] {details}");
    }
}

/// <summary>Прогресс бэкапа в консоль.</summary>
sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine("  " + value);
}
