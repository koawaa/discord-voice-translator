using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordVoiceTranslator.Core.Updates;

/// <summary>Une version plus récente, publiée sur la page des versions GitHub.</summary>
public sealed record AvailableUpdate(Version Version, string PageUrl);

/// <summary>
/// Demande à GitHub la dernière version publiée. Rien n'est envoyé d'autre que la requête
/// elle-même ; rien n'est téléchargé ni installé : on prévient seulement.
/// </summary>
public static class UpdateChecker
{
    private const string LatestRelease = "https://api.github.com/repos/koawaa/discord-voice-translator/releases/latest";
    private const string ReleasesPage = "https://github.com/koawaa/discord-voice-translator/releases/";

    /// <summary>
    /// La version publiée si elle est plus récente ; null si à jour, hors ligne ou réponse
    /// inattendue : vérifier ne doit jamais gêner l'appli.
    /// </summary>
    public static async Task<AvailableUpdate?> FindNewerAsync(Version current, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub refuse les requêtes sans User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DiscordVoiceTranslator/{current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var release = await http.GetFromJsonAsync<Release>(LatestRelease, cancellationToken);
            if (release is null || release.Draft || release.Prerelease
                || !Version.TryParse(release.TagName.TrimStart('v', 'V'), out var latest)
                // Le lien s'ouvre dans le navigateur : seulement s'il mène bien aux versions du projet.
                || !release.HtmlUrl.StartsWith(ReleasesPage, StringComparison.Ordinal))
                return null;
            return Normalize(latest) > Normalize(current) ? new AvailableUpdate(latest, release.HtmlUrl) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // « 1.6 », « 1.6.0 » et « 1.6.0.0 » sont la même version.
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);
}
