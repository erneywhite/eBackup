using System.Security.Cryptography;
using System.Text;
using eBackup.Core.Abstractions;
using eBackup.Core.Crypto;
using eBackup.Core.Engine;
using eBackup.Localization;
using eBackup.Storage;

namespace eBackup.Service.Jobs;

// UserProfilePaths — в родительском namespace eBackup.Service.

/// <summary>
/// Исполнитель ВОССТАНОВЛЕНИЯ в службе (под SYSTEM): тянет архив из хранилища во временный файл и
/// разворачивает движком с per-SID записью (профиль вызвавшего). Поэтому Program Files пишется
/// (нужны админ-права), а {APPDATA}/{LOCALAPPDATA} идут в профиль пользователя; restore-хуки (OBS)
/// тоже per-SID. Прогресс/лог — в шину задачи (журнал + живые подписчики).
/// Зашифрованные архивы (EBKE) расшифровываются по фразе из <see cref="Job.ResolvedPassphrase"/>
/// (тикет интерактива или машинный ключ расписания); fail-closed, если фраза затребована, но её нет.
/// </summary>
public sealed class RestoreRunner : IJobRunner
{
    private readonly StorageStore _storages;
    private readonly Func<IReadOnlyList<IBackupModule>> _resolveRestoreModules; // все рабочие модули (для хуков)
    private readonly string _buildDir;

    public static string DefaultBuildDir => Path.Combine(Path.GetTempPath(), "eBackup", "restore");

    public RestoreRunner(
        StorageStore storages,
        Func<IReadOnlyList<IBackupModule>> resolveRestoreModules,
        string? buildDir = null)
    {
        _storages = storages;
        _resolveRestoreModules = resolveRestoreModules;
        _buildDir = buildDir ?? DefaultBuildDir;
    }

    /// <summary>Подмести осиротевшие per-run папки восстановления (хвосты от падения процесса). Зовётся на старте службы.</summary>
    public static void SweepOrphanRuns(string? buildDir = null)
    {
        var dir = buildDir ?? DefaultBuildDir;
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var run in Directory.EnumerateDirectories(dir, "run-*"))
                try { Directory.Delete(run, recursive: true); } catch { /* temp */ }
        }
        catch { /* нет папки/нет доступа — не критично */ }
    }

    public async Task<JobOutcome> RunAsync(Job job, CancellationToken ct)
    {
        var sink = job.Channel;
        void Log(string m) => sink.Log(m);
        void WipePassphrase() { if (job.ResolvedPassphrase is { } pw) CryptographicOperations.ZeroMemory(pw); }
        var r = job.Restore!;

        // Fail-closed: затребована расшифровка, но фразы нет → не запускаем restore без неё.
        if (job.RequiresEncryption && (job.ResolvedPassphrase is null || job.ResolvedPassphrase.Length == 0))
        {
            Log("Восстановление отменено: затребована расшифровка, но парольная фраза недоступна.");
            return new JobOutcome(false, 0, 0, r.RemoteName, "Шифрование затребовано, но парольная фраза недоступна.");
        }

        var targetLabel = r.TargetDir ?? "исходные места";
        Log($"Запуск: {job.Trigger}");
        Log($"Восстановление: {r.RemoteName} → {targetLabel} · режим конфликтов: {r.Policy}");

        var saved = (await _storages.LoadAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == r.SourceStorageId)
            ?? throw new InvalidOperationException("Хранилище-источник не найдено.");
        var storage = StorageFactory.Create(saved, _storages.Protector);

        // Своя папка на прогон: и скачанный архив, и расшифрованный plaintext лежат тут и зачищаются целиком.
        var runDir = Path.Combine(_buildDir, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        var temp = Path.Combine(runDir, r.RemoteName);
        var passphrase = job.ResolvedPassphrase is { Length: > 0 } pwBytes ? Encoding.UTF8.GetString(pwBytes) : null;
        try
        {
            // Лёгкая проба фразы по началу архива (seek) — при опечатке НЕ качаем весь архив зря.
            if (passphrase is not null && storage is ISeekableArchiveStorage seekable)
            {
                sink.Phase(L.Get("Phase_VerifyingPassphrase"), 0);
                var ok = true;
                Stream? head = null;
                try
                {
                    head = await seekable.OpenSeekableReadAsync(r.RemoteName, ct).ConfigureAwait(false);
                    ok = await ArchiveCipher.VerifyPassphraseAsync(head, passphrase, ct).ConfigureAwait(false);
                }
                catch (InvalidDataException ex) { Log("Проба архива не удалась: " + ex.Message + " — продолжаю полным путём."); }
                finally { if (head is not null) await head.DisposeAsync().ConfigureAwait(false); }

                if (!ok)
                {
                    Log("✕ Неверная парольная фраза — архив не скачивался.");
                    return new JobOutcome(false, 0, 0, r.RemoteName, "Неверная парольная фраза.");
                }
            }

            sink.Phase(L.Get("Phase_Downloading", r.RemoteName, saved.Name), 0);
            await storage.DownloadAsync(r.RemoteName, temp, ct).ConfigureAwait(false);
            Log($"Архив получен: {new FileInfo(temp).Length / 1024.0 / 1024.0:0.#} МБ");

            var policy = Enum.TryParse<ConflictPolicy>(r.Policy, out var p) ? p : ConflictPolicy.BackupExisting;
            var resolveDest = new UserProfilePaths(job.OwnerSid).Resolve; // запись в профиль ВЫЗВАВШЕГО (per-SID)
            var progress = new Progress<string>(s => sink.Phase(s, 0));

            var engine = new BackupEngine();
            await engine.RestoreAsync(
                temp,
                _resolveRestoreModules(),
                policy,
                destinationRootOverride: r.TargetDir,
                assetsDirectory: r.AssetsDir,
                passphrase: passphrase,           // фраза (если есть) разрешена службой; null — незашифрованный
                progress: progress,
                log: Log,
                resolveDestination: resolveDest,
                tempDirectory: runDir,            // расшифрованный plaintext — в нашу run-папку (зачистится целиком)
                ct: ct).ConfigureAwait(false);

            var skipped = engine.LastRestoreSkippedCount; // занятые/недоступные файлы → «с ошибками», не провал
            if (skipped > 0)
                Log($"⚠ Пропущено занятых/недоступных файлов: {skipped}. Закрой использующие их программы и повтори при необходимости.");
            Log("Готово.");
            return new JobOutcome(true, skipped, 0, r.RemoteName, null);
        }
        finally
        {
            WipePassphrase(); // занулить фразу всегда
            // Вся run-папка (скачанный архив + расшифрованный plaintext) удаляется целиком.
            try { Directory.Delete(runDir, recursive: true); } catch { /* temp run-папка */ }
        }
    }
}
