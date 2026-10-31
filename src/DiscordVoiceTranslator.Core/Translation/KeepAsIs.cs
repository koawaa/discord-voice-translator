using System.Net;
using System.Text.RegularExpressions;

namespace DiscordVoiceTranslator.Core.Translation;

/// <summary>
/// Protège des mots de la traduction (pseudos, termes de jeu) en les entourant d'une balise
/// XML que le fournisseur recopie telle quelle, puis retire la balise du résultat.
/// </summary>
public static class KeepAsIs
{
    public const string Tag = "keep";

    /// <summary>
    /// Le texte échappé pour XML, termes balisés ; null si aucun terme n'y figure (le texte
    /// part alors tel quel, sans mode XML).
    /// </summary>
    public static string? Mark(string text, IReadOnlyCollection<string> terms)
    {
        if (terms.Count == 0)
            return null;
        // Les plus longs d'abord : « M4A1-S » avant « M4 ». Mots entiers seulement, sans tenir
        // compte de la casse (Whisper écrit « awp » comme « AWP »).
        // Termes échappés comme le texte : un pseudo « Tom&Jerry » se retrouve tel quel dans le texte échappé.
        string alternatives = string.Join("|", terms.Where(t => t.Length > 1).OrderByDescending(t => t.Length).Select(t => Regex.Escape(Escape(t))));
        if (alternatives.Length == 0)
            return null;
        var pattern = new Regex($@"(?<![\w-])(?:{alternatives})(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        bool found = false;
        string marked = pattern.Replace(Escape(text), match =>
        {
            found = true;
            return $"<{Tag}>{match.Value}</{Tag}>";
        });
        return found ? marked : null;
    }

    /// <summary>Retire les balises et rend le texte normal (le fournisseur répond en XML).</summary>
    public static string Unmark(string translated) =>
        WebUtility.HtmlDecode(translated.Replace($"<{Tag}>", "").Replace($"</{Tag}>", ""));

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
