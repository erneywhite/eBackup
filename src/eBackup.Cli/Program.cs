using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Modules.Obs;

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
        Console.WriteLine($"Готово: {path}");
        break;
    }

    case "restore":
    {
        var archive = GetOption(args, "--archive");
        if (archive is null)
        {
            Console.Error.WriteLine("Укажите --archive <путь к .ebk>");
            return 1;
        }
        await new BackupEngine().RestoreAsync(archive);
        Console.WriteLine("Восстановление завершено.");
        break;
    }

    default:
        Console.WriteLine(
            """
            eBackup CLI (v1, черновик)

            Команды:
              list-modules                            Список доступных модулей
              backup  --out <dir> [--name <имя>]      Создать .ebk из всех модулей
              restore --archive <путь.ebk>            Распаковать архив по манифесту
            """);
        break;
}

return 0;

static string? GetOption(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
