using System.Diagnostics;

namespace DiscordVoiceTranslator.Core.Timing;

/// <summary>
/// Horloge commune à tout le pipeline : compteur de performance Windows (QPC) en unités de
/// 100 ns, c'est-à-dire la même base que les positions QPC fournies par WASAPI. Les
/// événements Discord « parle / ne parle plus » seront horodatés sur cette même horloge.
/// </summary>
public static class QpcClock
{
    public const long TicksPerSecond = 10_000_000;

    public static long Now100ns => (long)((Int128)Stopwatch.GetTimestamp() * TicksPerSecond / Stopwatch.Frequency);

    public static double ToMilliseconds(long delta100ns) => delta100ns / 10_000.0;
}
