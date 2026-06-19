using System.Text.Json;
using CG.Web.MegaApiClient;

namespace eBackup.Storage;

/// <summary>
/// Подключение к MEGA «один раз»: вход паролем (и 2FA-кодом, если включена) даёт СЕССИЮ, которую мы
/// сохраняем и дальше переиспользуем без пароля и без 2FA — как refresh-токен OAuth-облаков. Сессию
/// сериализуем своим JSON (sessionId + masterKey) и храним зашифрованной машинным ключом службы.
/// ВАЖНО: <see cref="IMegaApiClient.LogoutAsync"/> убивает сессию на сервере — поэтому НИГДЕ не логаутимся
/// (ни при подключении, ни в операциях): сессия живёт, как у настольного клиента, пока её не отозвать.
/// </summary>
public static class MegaSession
{
    private sealed record Persisted(string SessionId, string MasterKeyB64);

    /// <summary>Войти e-mail+пароль (+опц. 2FA-код) и вернуть сериализованную сессию для хранения как секрет.</summary>
    public static async Task<string> ConnectAsync(string email, string password, string? mfaCode, CancellationToken ct = default)
    {
        var client = new MegaApiClient();
        var token = await client
            .LoginAsync(email, password, string.IsNullOrWhiteSpace(mfaCode) ? null : mfaCode.Trim())
            .ConfigureAwait(false);
        // НЕ логаутимся — сессия должна остаться валидной для последующего переиспользования службой.
        return JsonSerializer.Serialize(new Persisted(token.SessionId, Convert.ToBase64String(token.MasterKey)));
    }

    /// <summary>Восстановить сессию MEGA из сохранённого секрета.</summary>
    public static MegaApiClient.LogonSessionToken Deserialize(string json)
    {
        var p = JsonSerializer.Deserialize<Persisted>(json)
            ?? throw new InvalidOperationException("Повреждённая сохранённая сессия MEGA.");
        return new MegaApiClient.LogonSessionToken(p.SessionId, Convert.FromBase64String(p.MasterKeyB64));
    }
}
