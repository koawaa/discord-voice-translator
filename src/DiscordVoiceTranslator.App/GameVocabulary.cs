using System.IO;
using System.Text.Json;
using DiscordVoiceTranslator.Core.Audio;

namespace DiscordVoiceTranslator.App;

/// <summary>
/// Vocabulaire d'un jeu, activé tout seul quand l'un de ses exécutables tourne. Un fichier
/// JSON par jeu : ceux livrés avec l'appli (dossier « vocabulary »), plus ceux que l'on
/// ajoute soi-même dans %APPDATA%\DiscordVoiceTranslator\vocabulary.
/// </summary>
/// <param name="Terms">Du plus au moins prononcé : Whisper n'a de place que pour les premiers.</param>
internal sealed record GameVocabulary(string Game, IReadOnlyList<string> Exe, IReadOnlyList<string> Terms)
{
    public static readonly string UserFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscordVoiceTranslator", "vocabulary");

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    // Exécutables partagés avec d'autres programmes (Java, moteur Source, applis du Store) :
    // le jeu n'est reconnu que si l'une de ses fenêtres porte son nom (« Minecraft 1.21 »).
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "java.exe", "javaw.exe", "hl2.exe", "Windows10Universal.exe",
    };

    /// <summary>Les jeux de la liste qui tournent en ce moment.</summary>
    public static List<GameVocabulary> Running(IEnumerable<GameVocabulary> packs)
    {
        var running = ProcessLocator.RunningExeNames();
        // Lister les fenêtres prend quelques centaines de ms : seulement si un hôte partagé tourne.
        List<RunningApp>? windows = null;
        bool HasWindow(string exe, string game) =>
            (windows ??= ProcessLocator.ListWindowedApps())
                .Any(w => w.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase) && w.Title.Contains(game, StringComparison.OrdinalIgnoreCase));

        return packs.Where(p => p.Exe.Any(exe => running.Contains(exe) && (!SharedHosts.Contains(exe) || HasWindow(exe, p.Game)))).ToList();
    }

    /// <summary>Tous les fichiers valides ; un fichier abîmé est ignoré plutôt que de tout bloquer.</summary>
    public static List<GameVocabulary> LoadAll()
    {
        var packs = new List<GameVocabulary>();
        foreach (string folder in new[] { Path.Combine(AppContext.BaseDirectory, "vocabulary"), UserFolder })
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var pack = JsonSerializer.Deserialize<GameVocabulary>(File.ReadAllText(file), Json);
                    if (pack is { Game.Length: > 0, Exe.Count: > 0, Terms.Count: > 0 })
                        packs.Add(pack);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Fichier mal formé ou illisible : les autres jeux restent utilisables.
                }
            }
        }
        return packs;
    }
}
