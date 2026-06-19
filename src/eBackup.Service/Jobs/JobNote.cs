namespace eBackup.Service.Jobs;

/// <summary>Типы нотификаций задачи (на проводе едут note-фреймами через AttachToJob).</summary>
public static class JobNoteKind
{
    public const string Phase = "phase";        // крупная фаза + доля прогресса
    public const string Log = "log";            // подробная строка
    public const string State = "state";        // смена состояния
    public const string Completed = "completed"; // терминальная
}

/// <summary>Одна нотификация задачи с монотонным seq (= номер строки журнала этого запуска).</summary>
public sealed record JobNote(long Seq, string Kind, string RunId, string? Text, double Fraction, string? State);

/// <summary>Куда исполнитель шлёт прогресс: крупные фазы (с долей) и подробные строки лога.</summary>
public interface IJobSink
{
    void Phase(string text, double fraction);
    void Log(string text);
}
