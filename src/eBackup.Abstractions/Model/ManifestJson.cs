using System.Text.Json;
using System.Text.Json.Serialization;

namespace eBackup.Core.Model;

/// <summary>
/// Канонические настройки сериализации манифеста и дескрипторов модулей.
/// Единая политика (camelCase + строковые enum) гарантирует, что старые архивы
/// читаются одинаково и декларативный парсер не разойдётся с движком.
/// </summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
