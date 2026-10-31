using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Translation;

/// <summary>API REST DeepL v2 (offre gratuite ou Pro, choisie d'après la clé).</summary>
public sealed class DeepLTranslator : ITranslator
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Langues que DeepL accepte en source. Hors de cette liste, on le laisse détecter seul
    // plutôt que de se faire refuser la requête.
    private static readonly HashSet<string> SourceLanguages =
    [
        "AR", "BG", "CS", "DA", "DE", "EL", "EN", "ES", "ET", "FI", "FR", "HU", "ID", "IT", "JA",
        "KO", "LT", "LV", "NB", "NL", "PL", "PT", "RO", "RU", "SK", "SL", "SV", "TR", "UK", "ZH",
    ];

    private readonly HttpClient _http;

    public string Name => "DeepL";

    public DeepLTranslator(string apiKey)
    {
        // Les clés de l'offre gratuite finissent par « :fx » et passent par un autre serveur.
        string host = apiKey.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com/" : "https://api.deepl.com/";
        _http = new HttpClient { BaseAddress = new Uri(host), Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
    }

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, TranslationHints hints, CancellationToken cancellationToken)
    {
        string source = sourceLanguage.ToUpperInvariant();
        // Les termes à garder sont entourés d'une balise que DeepL recopie sans la traduire. Le
        // mode XML n'est activé que s'il y en a : il oblige à échapper le reste du texte.
        string? marked = KeepAsIs.Mark(text, hints.KeepAsIs);
        var request = new TranslateRequest([marked ?? text], targetLanguage, SourceLanguages.Contains(source) ? source : null)
        {
            // Le contexte n'est ni traduit ni facturé.
            Context = string.IsNullOrWhiteSpace(hints.Context) ? null : hints.Context,
            // « prefer_ » : simplement ignoré pour les langues sans tutoiement, au lieu d'une erreur.
            Formality = hints.Informal ? "prefer_less" : null,
            TagHandling = marked is null ? null : "xml",
            IgnoreTags = marked is null ? null : [KeepAsIs.Tag],
        };
        var response = await SendAsync(() => _http.PostAsJsonAsync("v2/translate", request, Json, cancellationToken), cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<TranslateResponse>(Json, cancellationToken);
        string translated = body?.Translations.FirstOrDefault()?.Text ?? throw new TranslationException(T("DeepL returned an empty answer.", "DeepL a renvoyé une réponse vide."));
        return marked is null ? translated : KeepAsIs.Unmark(translated);
    }

    public async Task<TranslatorUsage?> GetUsageAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(() => _http.GetAsync("v2/usage", cancellationToken), cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<UsageResponse>(Json, cancellationToken);
        return body is null ? null : new TranslatorUsage(body.CharacterCount, body.CharacterLimit);
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(T("DeepL is unreachable (internet connection?).", "DeepL est injoignable (connexion ?)."), ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationException(T("DeepL did not answer (over 5 s).", "DeepL ne répond pas (plus de 5 s)."), ex);
        }

        if (response.IsSuccessStatusCode)
            return response;
        response.Dispose();
        int status = (int)response.StatusCode;
        throw new TranslationException(status switch
        {
            (int)HttpStatusCode.Forbidden => T("DeepL key rejected.", "Clé DeepL refusée."),
            456 => T("DeepL monthly quota used up.", "Quota DeepL du mois épuisé."),
            (int)HttpStatusCode.TooManyRequests => T("Too many DeepL requests, try again in a moment.", "Trop de requêtes DeepL, réessaie dans un instant."),
            int code => T($"DeepL answered with error {code}.", $"DeepL a répondu par une erreur {code}."),
        }, keyUnusable: status is (int)HttpStatusCode.Forbidden or 456);
    }

    public void Dispose() => _http.Dispose();

    private sealed record TranslateRequest(string[] Text, string TargetLang, string? SourceLang)
    {
        public string? Context { get; init; }
        public string? Formality { get; init; }
        public string? TagHandling { get; init; }
        public string[]? IgnoreTags { get; init; }
    }
    private sealed record TranslateResponse(Translated[] Translations);
    private sealed record Translated(string Text);
    private sealed record UsageResponse(long CharacterCount, long CharacterLimit);
}
