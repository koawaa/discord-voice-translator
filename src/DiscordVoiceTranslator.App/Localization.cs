using System.Windows.Markup;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Texte bilingue dans le XAML : Text="{local:Tr En=Settings, Fr=Paramètres}". La langue est
/// fixée au démarrage, la valeur est donc lue une seule fois au chargement de la fenêtre.
/// (Dans le XAML, les apostrophes françaises sont typographiques, ’, pour ne pas fermer la valeur.)
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public string En { get; set; } = "";
    public string Fr { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => T(En, Fr);
}

/// <summary>Noms des langues parlées que Whisper peut reconnaître (codes ISO à deux lettres).</summary>
internal static class SpokenLanguages
{
    public static IReadOnlyList<(string Code, string English, string French)> All { get; } =
    [
        ("en", "English", "Anglais"),
        ("fr", "French", "Français"),
        ("es", "Spanish", "Espagnol"),
        ("de", "German", "Allemand"),
        ("it", "Italian", "Italien"),
        ("pt", "Portuguese", "Portugais"),
        ("nl", "Dutch", "Néerlandais"),
        ("pl", "Polish", "Polonais"),
        ("ru", "Russian", "Russe"),
        ("uk", "Ukrainian", "Ukrainien"),
        ("tr", "Turkish", "Turc"),
        ("ar", "Arabic", "Arabe"),
        ("ja", "Japanese", "Japonais"),
        ("ko", "Korean", "Coréen"),
        ("zh", "Chinese", "Chinois"),
    ];

    public static string Name(string code)
    {
        foreach (var language in All)
        {
            if (language.Code == code)
                return T(language.English, language.French);
        }
        return code.ToUpperInvariant();
    }
}
