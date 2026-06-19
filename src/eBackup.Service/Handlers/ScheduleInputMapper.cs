using eBackup.Core.Scheduling;
using eBackup.Ipc.Contracts;

namespace eBackup.Service.Handlers;

/// <summary>
/// Преобразование между IPC-DTO расписания и типизированной <see cref="BackupSchedule"/>.
/// Парольная фраза обрабатывается как секреты хранилищ: открытый текст шифруется машинным ключом
/// через переданный <see cref="ScheduleStore"/> и в модель попадает только Protected*-значение.
/// Чистое отображение без обращения ко времени — «сейчас» для NextRun/штампа создания передаёт хендлер.
/// </summary>
public static class ScheduleInputMapper
{
    /// <summary>
    /// DTO → BackupSchedule. <paramref name="existing"/> — прежняя версия при правке (сохраняем владельца,
    /// LastRunAt и «оставленную» фразу). <paramref name="ownerSid"/> — SID вызывающего (владелец нового).
    /// Семантика NewPassphrase: null — оставить прежнюю; "" — снять шифрование; иначе — задать.
    /// </summary>
    public static BackupSchedule ToSchedule(ScheduleInput i, ScheduleStore protector, BackupSchedule? existing, string ownerSid)
    {
        _ = Enum.TryParse<ScheduleKind>(i.Kind, ignoreCase: true, out var kind);

        return new BackupSchedule
        {
            Id = i.Id,
            Name = i.Name,
            OwnerSid = existing?.OwnerSid ?? ownerSid,         // владельца при правке не меняем
            ModuleIds = i.ModuleIds.ToList(),
            CustomFolders = i.CustomFolderIds.ToList(),
            TargetConnectionIds = i.TargetStorageIds.ToList(),
            KeepLocal = i.KeepLocal,
            ProtectedPassphrase = i.NewPassphrase switch
            {
                null => existing?.ProtectedPassphrase,          // оставить прежнюю
                "" => null,                                     // снять шифрование
                var p => protector.ProtectPassphrase(p),        // задать (машинный ключ)
            },
            Kind = kind,
            Hour = i.Hour,
            Minute = i.Minute,
            Days = i.Days.Select(d => (DayOfWeek)d).Distinct().ToList(),
            EveryHours = i.EveryHours,
            IdleMinutes = i.IdleMinutes,
            Enabled = i.Enabled,
            CompressionMode = i.CompressionMode,
            IncludeMachineName = i.IncludeMachineName,
            RetentionCount = i.RetentionCount,
            LastRunAt = existing?.LastRunAt,                     // штамп «сейчас» для нового ставит хендлер
        };
    }

    /// <summary>
    /// BackupSchedule → запрос бэкапа для очереди службы (ручной «выполнить сейчас» и таймер S6-5).
    /// Шифрование не передаётся: на S6 зашифрованные расписания в принципе не запускаются (ждут S7).
    /// </summary>
    public static StartBackupRequest ToBackupRequest(BackupSchedule s) => new()
    {
        ModuleIds = s.ModuleIds.ToArray(),
        CustomFolderIds = s.CustomFolders.ToArray(),
        TargetStorageIds = s.TargetConnectionIds.ToArray(),
        CompressionMode = s.CompressionMode,
        Passphrase = null,                       // S6: без шифрования (зашифрованные расписания отклоняются)
        IncludeMachineName = s.IncludeMachineName,
        RetentionCount = s.RetentionCount,
        Trigger = $"по расписанию «{s.Name}»",
        ClientRequestId = "",                    // служба не повторяет запрос — идемпотентность не нужна
    };

    /// <summary>BackupSchedule → сводка для GUI (без секрета фразы; только факт её наличия + расчёт NextRun).</summary>
    public static ScheduleSummary ToSummary(BackupSchedule s, DateTime now)
    {
        var next = ScheduleTiming.NextRun(s, now);
        return new ScheduleSummary
        {
            Id = s.Id,
            Name = s.Name,
            Kind = s.Kind.ToString(),
            Enabled = s.Enabled,
            HasPassphrase = s.ProtectedPassphrase is not null,
            OwnerSid = s.OwnerSid,
            LastRunAt = s.LastRunAt is { } l ? new DateTimeOffset(l) : null,
            NextRun = next is { } n ? new DateTimeOffset(n) : null,
        };
    }
}
