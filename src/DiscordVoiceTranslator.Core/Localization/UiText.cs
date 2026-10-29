namespace DiscordVoiceTranslator.Core.Localization;

/// <summary>
/// Langue de l'interface : anglais par défaut, français en option. Chaque texte est écrit
/// sur place dans les deux langues (T("Settings", "Paramètres")), ce qui reste lisible pour
/// une application de cette taille. Fixée au démarrage, avant d'afficher quoi que ce soit.
/// </summary>
public static class UiText
{
    public static bool French { get; set; }

    public static string T(string english, string french) => French ? french : english;
}
