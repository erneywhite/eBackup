using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using eBackup.Core.Scheduling;
using eBackup.Core.Security;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Platform;
using eBackup.Security;
using eBackup.Storage;

namespace eBackup.App;

/// <summary>
/// Одноразовый перенос конфигов из раскладки до 1.2 (per-user %APPDATA%/%LOCALAPPDATA%, секреты под
/// DPAPI текущего пользователя) в службу 1.2 (машинный конфиг в ProgramData под машинным ключом).
///
/// Служба (LocalSystem) НЕ может расшифровать DPAPI-секреты пользователя, поэтому перенос идёт здесь,
/// под пользователем: читаем старое → расшифровываем локально → пере-заливаем через IPC, где служба
/// перешифрует машинным ключом. Записи, уже существующие в службе (по id), не трогаем — без дублей и
/// затирания. Каждый элемент в своём try/catch: один битый секрет не должен сорвать весь перенос.
///
/// NB (хвост на потом): хранилища/папки/модули в службе машинные (без SID), id — slug по имени. На
/// мультипользовательской машине, где ОБА юзера в 1.1 имели одноимённые хранилища, второй перенос
/// пропустит коллизию по id — это известное ограничение машинной модели, не для одиночного апгрейда.
/// </summary>
public static class LegacyConfigMigrator
{
    public sealed record Result(int Storages, int Schedules, int Folders, int Modules, int DisabledModules,
        int SchedulesNeedPassphrase)
    {
        public int Total => Storages + Schedules + Folders + Modules + DisabledModules;
    }

    public static async Task<Result> MigrateAsync(IpcClient client)
    {
        int storages = 0, schedules = 0, folders = 0, modules = 0, disabled = 0, needPassphrase = 0;
        var dpapi = new DpapiSecretProtector();

        // --- Хранилища (%LOCALAPPDATA%\eBackup\storages.json, DPAPI) ---
        if (File.Exists(AppPaths.StoragesFile))
        {
            var legacy = await SafeLoadAsync(() => new StorageStore(dpapi, AppPaths.StoragesFile).LoadAsync());
            if (legacy.Count > 0)
            {
                var present = (await client.ListStorageDetailsAsync())
                    .Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var s in legacy)
                {
                    if (present.Contains(s.Id)) continue;
                    try { await client.UpsertStorageAsync(ToInput(s, dpapi)); storages++; } catch { }
                }
            }
        }

        // --- Расписания (%APPDATA%\eBackup\schedules.json, DPAPI) ---
        if (File.Exists(AppPaths.SchedulesFile))
        {
            var store = new ScheduleStore(dpapi, AppPaths.SchedulesFile);
            var legacy = await SafeLoadAsync(() => store.LoadAsync());
            if (legacy.Count > 0)
            {
                var present = (await client.ListSchedulesAsync())
                    .Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var sc in legacy)
                {
                    if (present.Contains(sc.Id)) continue;

                    string? passphrase = null;
                    if (sc.ProtectedPassphrase is not null)
                    {
                        // fail-closed: зашифрованное расписание без расшифрованной фразы НЕ переносим — иначе
                        // оно молча начало бы делать незашифрованные архивы. Пусть пользователь пересоздаст.
                        try { passphrase = store.UnprotectPassphrase(sc.ProtectedPassphrase); }
                        catch { needPassphrase++; continue; }
                    }
                    try { await client.UpsertScheduleAsync(ToInput(sc, passphrase)); schedules++; } catch { }
                }
            }
        }

        // --- Свои папки (%APPDATA%\eBackup\custom-folders.json) ---
        try
        {
            var legacyFolders = CustomFolderConfig.Load();
            if (legacyFolders.Count > 0)
            {
                var present = (await client.ListCustomFoldersAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in legacyFolders)
                {
                    if (string.IsNullOrWhiteSpace(f) || present.Contains(f)) continue;
                    try { await client.UpsertCustomFolderAsync(f); folders++; } catch { }
                }
            }
        }
        catch { }

        // --- Drop-in модули (%APPDATA%\eBackup\modules\*.module.json) — до выключенных, чтобы было что выключать ---
        try
        {
            if (Directory.Exists(AppPaths.ModulesDir))
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.ModulesDir, "*.module.json"))
                {
                    try { await client.InstallModuleAsync(File.ReadAllText(file)); modules++; } catch { }
                }
            }
        }
        catch { }

        // --- Выключенные модули (%APPDATA%\eBackup\disabled-modules.json) ---
        try
        {
            if (File.Exists(AppPaths.DisabledModulesFile))
            {
                var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(AppPaths.DisabledModulesFile)) ?? [];
                foreach (var id in ids.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    try { await client.SetModuleEnabledAsync(id, false); disabled++; } catch { }
                }
            }
        }
        catch { }

        return new Result(storages, schedules, folders, modules, disabled, needPassphrase);
    }

    private static async Task<IReadOnlyList<T>> SafeLoadAsync<T>(Func<Task<IReadOnlyList<T>>> load)
    {
        try { return await load(); } catch { return []; }
    }

    /// <summary>SavedStorage → StorageInput (зеркало StorageInputMapper в службе: те же ключи settings/секретов).
    /// Секреты расшифровываются локально (DPAPI пользователя) — служба перешифрует их машинным ключом.</summary>
    private static StorageInput ToInput(SavedStorage s, ISecretProtector dpapi)
    {
        var settings = new Dictionary<string, string>();
        void Put(string k, string? v) { if (!string.IsNullOrEmpty(v)) settings[k] = v!; }
        Put("path", s.Path);
        Put("shareUsername", s.ShareUsername);
        Put("host", s.Host);
        settings["port"] = s.Port.ToString();
        Put("username", s.Username);
        Put("remoteDirectory", s.RemoteDirectory);
        settings["useFtps"] = s.UseFtps.ToString();
        settings["allowUntrustedCertificate"] = s.AllowUntrustedCertificate.ToString();
        Put("serviceUrl", s.ServiceUrl);
        Put("bucket", s.Bucket);
        Put("accessKeyId", s.AccessKeyId);
        settings["forcePathStyle"] = s.ForcePathStyle.ToString();

        var secrets = new Dictionary<string, string>();
        void Dec(string k, string? prot)
        {
            if (prot is null) return;
            try { secrets[k] = dpapi.Unprotect(prot); } catch { /* нерасшифровываемый секрет пропускаем */ }
        }
        Dec("sharePassword", s.ProtectedSharePassword);
        Dec("password", s.ProtectedPassword);
        Dec("privateKey", s.ProtectedPrivateKey);
        Dec("keyPassphrase", s.ProtectedKeyPassphrase);
        Dec("secretKey", s.ProtectedSecretKey);
        Dec("oauthToken", s.ProtectedOAuthToken);

        return new StorageInput
        {
            Id = s.Id,
            Name = s.Name,
            Kind = s.Kind.ToString(),
            Settings = settings,
            PlaintextSecrets = secrets.Count > 0 ? secrets : null,
        };
    }

    /// <summary>BackupSchedule → ScheduleInput. Фраза уже расшифрована вызывающим (локально, DPAPI) и едет
    /// открытой один раз — служба перешифрует её машинным ключом (как при обычном сохранении расписания).
    /// Если у расписания была фраза, но расшифровать не удалось, вызывающий его НЕ переносит (fail-closed).</summary>
    private static ScheduleInput ToInput(BackupSchedule s, string? passphrase)
    {
        return new ScheduleInput
        {
            Id = s.Id,
            Name = s.Name,
            ModuleIds = s.ModuleIds.ToArray(),
            CustomFolderIds = s.CustomFolders.ToArray(),
            TargetStorageIds = s.TargetConnectionIds.ToArray(),
            Kind = s.Kind.ToString(),
            Hour = s.Hour,
            Minute = s.Minute,
            Days = s.Days.Select(d => (int)d).ToArray(),
            EveryHours = s.EveryHours,
            IdleMinutes = s.IdleMinutes,
            Enabled = s.Enabled,
            CompressionMode = s.CompressionMode,
            IncludeMachineName = s.IncludeMachineName,
            RetentionCount = s.RetentionCount,
            NewPassphrase = passphrase,
        };
    }
}
