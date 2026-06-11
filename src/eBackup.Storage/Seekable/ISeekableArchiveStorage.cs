namespace eBackup.Storage;

/// <summary>
/// Хранилище, умеющее отдавать удалённый архив как поток с произвольным доступом.
/// ZIP читается «оглавлением и кусками»: браузер архива показывает содержимое и
/// извлекает выбранные файлы, не скачивая архив целиком (важно для 100+ ГБ).
/// </summary>
public interface ISeekableArchiveStorage
{
    /// <summary>
    /// Открыть удалённый файл на чтение с Seek. Поток не обязан быть
    /// потокобезопасным — одна операция за раз. Закрытие потока освобождает
    /// сетевые ресурсы (соединение/клиент).
    /// </summary>
    Task<Stream> OpenSeekableReadAsync(string remoteName, CancellationToken ct = default);
}
