using eBackup.Core.Model;

namespace eBackup.Core.Abstractions;

/// <summary>
/// Модуль описывает, ЧТО нужно бэкапить для конкретного приложения (например, OBS).
/// Декларативная часть (пути/маски) задаётся в дескрипторе модуля; код-хук
/// <see cref="DiscoverAsync"/> позволяет умно находить расположения в рантайме
/// (например, искать установку по реестру или запущенному процессу).
/// </summary>
public interface IBackupModule
{
    /// <summary>Стабильный идентификатор модуля, напр. "obs".</summary>
    string Id { get; }

    /// <summary>Отображаемое имя, напр. "OBS Studio".</summary>
    string DisplayName { get; }

    /// <summary>Найти все записи (файлы/папки/ключи реестра), которые нужно включить в бэкап.</summary>
    Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default);
}

/// <summary>
/// Опциональная добавка к <see cref="IBackupModule"/>: модуль, которому для ОБНАРУЖЕНИЯ источников
/// нужен профиль ВЫЗВАВШЕГО пользователя, а не процесса. Движок внедряет резолвер токенов ДО
/// <see cref="IBackupModule.DiscoverAsync"/>. Нужен модулям, читающим файлы на этапе обнаружения
/// (напр. OBS парсит сцены, чтобы найти внешние ассеты) — иначе служба под SYSTEM смотрит в
/// systemprofile вместо профиля пользователя. Append-only: существующие модули не затрагиваются.
/// </summary>
public interface IUserScopedDiscovery
{
    /// <summary>Резолвер «{APPDATA}/…» → абсолютный путь в профиле вызвавшего. Зовётся ДО DiscoverAsync.</summary>
    void UseSourceResolver(Func<string, string> resolveToken);
}
