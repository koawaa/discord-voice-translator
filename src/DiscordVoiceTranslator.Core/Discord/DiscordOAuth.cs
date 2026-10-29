using System.Text.Json;
using System.Text.Json.Nodes;
using DiscordVoiceTranslator.Core.Config;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Discord;

/// <summary>Identifiants de l'application créée sur le portail développeur Discord.</summary>
public sealed record DiscordAppCredentials(string ClientId, string ClientSecret)
{
    private const string SecretName = "discord-app";

    public static DiscordAppCredentials? Load() =>
        SecretStore.Load(SecretName) is string json ? JsonSerializer.Deserialize<DiscordAppCredentials>(json) : null;

    public void Save() => SecretStore.Save(SecretName, JsonSerializer.Serialize(this));

    public static void Delete() => SecretStore.Delete(SecretName);
}

public sealed record DiscordToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    private const string SecretName = "discord-token";

    public static DiscordToken? Load() =>
        SecretStore.Load(SecretName) is string json ? JsonSerializer.Deserialize<DiscordToken>(json) : null;

    public void Save() => SecretStore.Save(SecretName, JsonSerializer.Serialize(this));

    /// <summary>Oublie l'autorisation : Discord la redemandera à la prochaine connexion.</summary>
    public static void Forget() => SecretStore.Delete(SecretName);
}

/// <summary>Échange du code d'autorisation contre un jeton, et rafraîchissement du jeton.</summary>
internal static class DiscordOAuth
{
    /// <summary>
    /// À déclarer tel quel dans le portail (OAuth2 → Redirects) : Discord vérifie que l'URI
    /// envoyée lors de l'échange fait partie de celles de l'application.
    /// </summary>
    public const string RedirectUri = "http://localhost";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static Task<DiscordToken> ExchangeCodeAsync(DiscordAppCredentials app, string code, CancellationToken cancellationToken) =>
        RequestAsync(app, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
        }, cancellationToken);

    public static Task<DiscordToken> RefreshAsync(DiscordAppCredentials app, string refreshToken, CancellationToken cancellationToken) =>
        RequestAsync(app, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancellationToken);

    private static async Task<DiscordToken> RequestAsync(DiscordAppCredentials app, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        form["client_id"] = app.ClientId;
        form["client_secret"] = app.ClientSecret;
        using var response = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form), cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new DiscordRpcException(T($"Discord refused the OAuth2 exchange ({(int)response.StatusCode}): {body}",
                                            $"Discord a refusé l'échange OAuth2 ({(int)response.StatusCode}) : {body}"));

        var json = JsonNode.Parse(body)!;
        return new DiscordToken(
            json["access_token"]!.GetValue<string>(),
            json["refresh_token"]!.GetValue<string>(),
            DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]!.GetValue<int>()));
    }
}
