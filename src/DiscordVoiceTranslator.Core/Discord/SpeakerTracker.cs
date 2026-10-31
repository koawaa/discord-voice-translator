namespace DiscordVoiceTranslator.Core.Discord;

/// <summary>
/// Garde les plages « parle / ne parle plus » de chaque personne, horodatées sur QpcClock,
/// et dit à qui appartient un segment audio.
/// </summary>
public sealed class SpeakerTracker
{
    private const long Open = long.MaxValue;
    private static readonly long KeepFor = TimeSpan.FromMinutes(2).Ticks;
    // Si Discord n'envoie jamais le « ne parle plus » (événement perdu), la personne ne doit
    // pas se voir attribuer toutes les phrases suivantes : une plage ouverte s'arrête là.
    // Discord envoie « ne parle plus » dès une courte pause : 30 s sans pause n'arrive guère.
    private static readonly long MaxOpen = TimeSpan.FromSeconds(30).Ticks;

    // Décalage mesuré entre les événements Discord et le son capté : le son commence ~250 ms
    // après « parle » (tampon de Discord) et finit ~150 ms avant « ne parle plus » (Discord
    // attend un peu avant de déclarer le silence). Point de départ, affiné ensuite en direct.
    private static readonly long DefaultStartLag = Ms(250), DefaultEndLag = Ms(-150);
    private static readonly long MinStartLag = Ms(-300), MaxStartLag = Ms(900);
    private static readonly long MinEndLag = Ms(-900), MaxEndLag = Ms(300);
    private const int CalibrationSamples = 31;
    // Personne d'autre dans ce voisinage : la phrase est sûrement à elle seule, elle sert à calibrer.
    private static readonly long CalibrationClearance = Ms(1500);

    // Une deuxième personne n'est citée que si elle explique une part du son que la première
    // n'explique pas : au moins ça, et au moins cette part de la phrase. Un bruit sur un micro
    // ou un voisin qui finissait juste avant n'expliquent presque rien de plus.
    private static readonly long MinExclusive = Ms(400);
    private const double MinExclusiveShare = 0.15;
    // Couvertures à moins de 10 % l'une de l'autre : départagées par l'alignement des bords.
    private const double CoverageTie = 0.9;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<(long Start, long End)>> _intervals = new();
    private readonly List<long> _startLags = new(), _endLags = new();
    private long _lastCalibrated = -1;

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
    /// Personnes qui parlaient pendant [start, end] (début et fin de la parole dans le son),
    /// la principale en premier.
    /// <list type="bullet">
    /// <item>Les plages Discord sont d'abord recalées sur le son (décalage mesuré en direct).</item>
    /// <item>Principale : celle qui couvre le plus la phrase ; à quasi-égalité, celle dont les
    /// bords tombent le mieux (deux micros verts en même temps, dont un pour un bruit).</item>
    /// <item>Une autre n'est ajoutée (« A / B ») que si elle couvre une part que la principale
    /// ne couvre pas : quelqu'un qui enchaîne sans pause, pas un voisin qui finissait juste avant.</item>
    /// </list>
    /// </summary>
    /// <param name="margin100ns">Tolérance autour des plages recalées (gigue de Discord).</param>
    public IReadOnlyList<string> Attribute(long start, long end, long margin100ns, long now)
    {
        lock (_lock)
        {
            long startLag = Median(_startLags, DefaultStartLag);
            long endLag = Median(_endLags, DefaultEndLag);

            var candidates = new List<Candidate>();
            foreach (var (userId, list) in _intervals)
            {
                // Plages dans l'ordre : la première qui touche la phrase donne son début, la dernière sa fin.
                var ranges = new List<(long, long)>();
                long firstStart = 0, lastEnd = 0;
                foreach (var (s, e) in list)
                {
                    long from = s + startLag - margin100ns;
                    long to = (e == Open ? Math.Min(now, s + MaxOpen) : e + endLag) + margin100ns;
                    if (to <= start || from >= end)
                        continue;
                    if (ranges.Count == 0)
                        firstStart = s;
                    // Encore ouverte : Discord la dit en train de parler jusqu'à maintenant. Si le son
                    // s'est tu depuis un moment, ce n'est sans doute pas sa voix.
                    lastEnd = e == Open ? Math.Min(now, s + MaxOpen) : e;
                    ranges.Add((Math.Max(from, start), Math.Min(to, end)));
                }
                if (ranges.Count == 0)
                    continue;
                ranges = Merge(ranges);
                // Écart entre les bords du son et ceux de la plage recalée.
                long fit = Math.Abs(start - (firstStart + startLag)) + Math.Abs(end - (lastEnd + endLag));
                candidates.Add(new Candidate(userId, ranges, Length(ranges), fit));
            }

            Calibrate(start, end);
            if (candidates.Count == 0)
                return [];

            long best = candidates.Max(c => c.Coverage);
            var main = candidates.Where(c => c.Coverage >= best * CoverageTie).MinBy(c => c.Fit)!;

            long needed = Math.Max(MinExclusive, (long)((end - start) * MinExclusiveShare));
            var others = candidates
                .Where(c => c != main)
                .Select(c => (Candidate: c, Exclusive: Length(Subtract(c.Ranges, main.Ranges))))
                .Where(c => c.Exclusive >= needed)
                .OrderByDescending(c => c.Exclusive)
                .Take(2)
                .Select(c => c.Candidate);
            // Dans l'ordre où elles ont parlé : « A / B » quand A a commencé la phrase.
            return others.Append(main).OrderBy(c => c.Ranges[0].Start).Select(c => c.UserId).ToList();
        }
    }

    private sealed record Candidate(string UserId, List<(long Start, long End)> Ranges, long Coverage, long Fit);

    /// <summary>
    /// Une phrase sans personne d'autre autour et une seule plage fermée qui la recouvre : son
    /// décalage réel avec Discord affine celui des suivantes (connexion, tampon de chacun).
    /// </summary>
    private void Calibrate(long start, long end)
    {
        // La même phrase est attribuée plusieurs fois (personne masquée ?, puis affichage) : comptée une fois.
        if (start == _lastCalibrated)
            return;
        _lastCalibrated = start;
        (long S, long E)? only = null;
        foreach (var list in _intervals.Values)
        {
            foreach (var (s, e) in list)
            {
                long stop = e == Open ? long.MaxValue : e;
                if (stop < start - CalibrationClearance || s > end + CalibrationClearance)
                    continue;
                if (only is not null || e == Open)
                    return;
                only = (s, e);
            }
        }
        if (only is not { } range || range.E <= start || range.S >= end)
            return;
        Add(_startLags, start - range.S, MinStartLag, MaxStartLag);
        Add(_endLags, end - range.E, MinEndLag, MaxEndLag);
    }

    // Valeur aberrante (phrase coupée, événement en retard) : ignorée plutôt que de fausser la médiane.
    private static void Add(List<long> samples, long value, long min, long max)
    {
        if (value < min || value > max)
            return;
        samples.Add(value);
        if (samples.Count > CalibrationSamples)
            samples.RemoveAt(0);
    }

    // Peu de mesures : on garde la valeur par défaut, une seule phrase ne doit pas tout décaler.
    private static long Median(List<long> samples, long fallback)
    {
        if (samples.Count < 3)
            return fallback;
        var sorted = samples.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static long Length(List<(long Start, long End)> ranges) => ranges.Sum(r => r.End - r.Start);

    // Deux plages rapprochées se chevauchent une fois élargies : comptées une seule fois.
    private static List<(long Start, long End)> Merge(List<(long Start, long End)> ranges)
    {
        var merged = new List<(long Start, long End)>();
        foreach (var (s, e) in ranges.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && s <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            else
                merged.Add((s, e));
        }
        return merged;
    }

    /// <summary>Les parties de <paramref name="ranges"/> hors de <paramref name="removed"/>.</summary>
    private static List<(long Start, long End)> Subtract(List<(long Start, long End)> ranges, List<(long Start, long End)> removed)
    {
        var pieces = ranges;
        foreach (var (rs, re) in removed)
        {
            var next = new List<(long, long)>();
            foreach (var (s, e) in pieces)
            {
                if (re <= s || rs >= e) { next.Add((s, e)); continue; }
                if (rs > s) next.Add((s, rs));
                if (re < e) next.Add((re, e));
            }
            pieces = next;
        }
        return pieces;
    }

    private static long Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds).Ticks;

    private void Prune(long now)
    {
        foreach (var list in _intervals.Values)
            list.RemoveAll(i => i.End != Open && i.End < now - KeepFor);
    }
}
