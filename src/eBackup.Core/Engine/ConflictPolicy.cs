namespace eBackup.Core.Engine;

/// <summary>Что делать, если при восстановлении файл уже существует.</summary>
public enum ConflictPolicy
{
    /// <summary>Переименовать существующий в *.bak, затем записать новый (по умолчанию).</summary>
    BackupExisting,

    /// <summary>Перезаписать без сохранения старого.</summary>
    Overwrite,

    /// <summary>Не трогать существующий файл.</summary>
    Skip
}
