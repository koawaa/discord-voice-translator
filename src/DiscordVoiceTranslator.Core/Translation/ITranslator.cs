using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Translation;

/// <summary>Erreur de traduction avec un message lisible tel quel par l'utilisateur.</summary>
public sealed class TranslationException(string message, Exception? inner = null, bool keyUnusable = false) : Exception(message, inner)
{
    /// <summary>Clé refusée ou quota épuisé : une autre clé peut prendre le relais.</summary>
    public bool KeyUnusable { get; } = keyUnusable;
}

public sealed record TranslatorUsage(long CharactersUsed, long CharacterLimit);

/// <summary>Fournisseur de traduction : DeepL aujourd'hui, d'autres plus tard.</summary>
public interface ITranslator : IDisposable
{
    string Name { get; }

    /// <param name="sourceLanguage">Code ISO à deux lettres détecté par Whisper.</param>
    /// <param name="targetLanguage">Code du fournisseur, par exemple « FR » ou « EN-US ».</param>
    /// <exception cref="TranslationException">Clé refusée, quota épuisé, service injoignable...</exception>
    Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken);

    /// <summary>Consommation du forfait, ou null si le fournisseur ne la donne pas.</summary>
    Task<TranslatorUsage?> GetUsageAsync(CancellationToken cancellationToken);
}

/// <summary>Langue vers laquelle on traduit (code DeepL, nom en anglais et en français).</summary>
public sealed record TargetLanguage(string Code, string EnglishName, string FrenchName)
{
    public string DisplayName => T(EnglishName, FrenchName);

    public static IReadOnlyList<TargetLanguage> All { get; } =
    [
        new("EN-US", "English (US)", "Anglais (États-Unis)"),
        new("EN-GB", "English (UK)", "Anglais (Royaume-Uni)"),
        new("FR", "French", "Français"),
        new("ES", "Spanish", "Espagnol"),
        new("DE", "German", "Allemand"),
        new("IT", "Italian", "Italien"),
        new("PT-BR", "Portuguese (Brazil)", "Portugais (Brésil)"),
        new("PT-PT", "Portuguese (Portugal)", "Portugais (Portugal)"),
        new("NL", "Dutch", "Néerlandais"),
        new("PL", "Polish", "Polonais"),
        new("RU", "Russian", "Russe"),
        new("UK", "Ukrainian", "Ukrainien"),
        new("TR", "Turkish", "Turc"),
        new("JA", "Japanese", "Japonais"),
        new("KO", "Korean", "Coréen"),
        new("ZH-HANS", "Chinese (simplified)", "Chinois (simplifié)"),
    ];

    public static TargetLanguage Find(string code) =>
        All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>Vrai si Whisper a détecté cette langue (« EN-US » correspond à « en »).</summary>
    public bool Matches(string whisperLanguage) =>
        Code.Split('-')[0].Equals(whisperLanguage, StringComparison.OrdinalIgnoreCase);
}
