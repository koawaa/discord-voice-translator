namespace DiscordVoiceTranslator.Core.Discord;

/// <summary>
/// Garde les plages « parle / ne parle plus » de chaque personne, horodatées sur QpcClock,
/// et dit à qui appartient un segment audio : la personne dont les plages le recouvrent le plus.
/// </summary>
public sealed class SpeakerTracker
{
    private const long Open = long.MaxValue;
    private static readonly long KeepFor = TimeSpan.FromMinutes(2).Ticks;
    // Si Discord n'envoie jamais le « ne parle plus » (événement perdu), la personne ne doit
    // pas se voir attribuer toutes les phrases suivantes : une plage ouverte s'arrête là.
    // Discord envoie « ne parle plus » dès une courte pause : 30 s sans pause n'arrive guère.
    private static readonly long MaxOpen = TimeSpan.FromSeconds(30).Ticks;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<(long Start, long End)>> _intervals = new();

    /// <summary>Toi : ton micro n'est pas dans l'audio capté, tes événements sont ignorés.</summary>
    public string? IgnoredUserId { get; set; }

    public void Started(string userId, long qpc)
    {
        if (userId == IgnoredUserId) return;
        lock (_lock)
        {
            if (!_intervals.TryGetValue(userId, out var list))
                _intervals[userId] = list = new();
            if (list.Count == 0 || list[^1].End != Open)
                list.Add((qpc, Open));
            Prune(qpc);
        }
    }

    public void Stopped(string userId, long qpc)
    {
        lock (_lock)
        {
            if (_intervals.TryGetValue(userId, out var list) && list.Count > 0 && list[^1].End == Open)
                list[^1] = (list[^1].Start, qpc);
        }
    }

    public void Clear()
    {
        lock (_lock) _intervals.Clear();
    }

    /// <summary>
    /// Personnes qui parlaient pendant [start, end], la plus présente en premier. Une deuxième
    /// (ou troisième) personne n'est citée que si elle recouvre au moins la moitié de la
    /// première : deux voix mêlées s'affichent « A / B » plutôt que d'en choisir une au hasard.
    /// </summary>
    /// <param name="margin100ns">
    /// Élargit chaque plage des deux côtés : les événements Discord ne tombent pas pile sur
    /// l'audio joué (tampon de gigue, détection de parole de Discord).
    /// </param>
    public IReadOnlyList<string> Attribute(long start, long end, long margin100ns, long now)
    {
        var scores = new List<(string UserId, long Overlap)>();
        lock (_lock)
        {
            foreach (var (userId, list) in _intervals)
            {
                long overlap = 0;
                foreach (var (s, e) in list)
                {
                    long from = Math.Max(start, s - margin100ns);
                    long stop = e == Open ? Math.Min(now, s + MaxOpen) : e;
                    long to = Math.Min(end, stop + margin100ns);
                    if (to > from) overlap += to - from;
                }
                if (overlap > 0)
                    scores.Add((userId, overlap));
            }
        }
        if (scores.Count == 0)
            return [];

        scores.Sort((a, b) => b.Overlap.CompareTo(a.Overlap));
        long best = scores[0].Overlap;
        return scores.TakeWhile((s, i) => i < 3 && s.Overlap * 2 >= best).Select(s => s.UserId).ToList();
    }

    private void Prune(long now)
    {
        foreach (var list in _intervals.Values)
            list.RemoveAll(i => i.End != Open && i.End < now - KeepFor);
    }
}
