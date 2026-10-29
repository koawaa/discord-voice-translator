using System.Windows.Media;

namespace DiscordVoiceTranslator.App;

/// <summary>Couleur stable par personne : la même clé (ID Discord) donne toujours la même couleur.</summary>
internal static class SpeakerColors
{
    // Teintes claires, lisibles sur fond sombre et bien distinctes entre elles.
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x74, 0xC0, 0xFC), Color.FromRgb(0xFF, 0xA9, 0x4D), Color.FromRgb(0x8C, 0xE9, 0x9A),
        Color.FromRgb(0xFF, 0xD4, 0x3B), Color.FromRgb(0xDA, 0x77, 0xF2), Color.FromRgb(0x66, 0xD9, 0xE8),
        Color.FromRgb(0xFF, 0x87, 0x87), Color.FromRgb(0xA9, 0xE3, 0x4B), Color.FromRgb(0xF7, 0x83, 0xAC),
        Color.FromRgb(0x91, 0xA7, 0xFF),
    ];

    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public static Brush For(string key)
    {
        if (Cache.TryGetValue(key, out var brush))
            return brush;
        brush = new SolidColorBrush(Palette[Fnv1a(key) % (uint)Palette.Length]);
        brush.Freeze();
        Cache[key] = brush;
        return brush;
    }

    // string.GetHashCode() change à chaque lancement (.NET le randomise) : la couleur d'une
    // personne changerait d'une session à l'autre. FNV-1a est stable et suffit ici.
    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }
}
