namespace z3l.Scraper.Loading;

/// <summary>
/// How a list rests what it gives out (a proxy, a user agent): its block policy, failures and bad responses in a row, pauses, and how the
/// rests grow: every rest of the item (for every site or for one) is <paramref name="RestGrowth"/> times the one before, up to
/// <paramref name="MaxRest"/>; 1 for rests that don't grow.
/// </summary>
internal readonly record struct RestRules(BlockPolicy BlockPolicy, int MaxConsecutiveFailures, TimeSpan FailurePause,
    int MaxConsecutiveBadResponses, TimeSpan BadResponsePause, double RestGrowth = 1, TimeSpan MaxRest = default)
{
    /// <summary>Good loads in a row after which a rest of the item is forgiven: the next one is shorter by a step.</summary>
    public const int GoodInARowToForgive = 5;

    /// <summary>
    /// The rest after <paramref name="strikes"/> rests of the item: <paramref name="rest"/> grown <see cref="RestGrowth"/> times for
    /// each, but not over <see cref="MaxRest"/> (a rest longer than MaxRest by itself stays as it is).
    /// </summary>
    public TimeSpan Grown(TimeSpan rest, int strikes)
    {
        if (strikes <= 0 || RestGrowth <= 1 || rest <= TimeSpan.Zero || rest == TimeSpan.MaxValue) return rest;
        var max = MaxRest > rest ? MaxRest : rest;
        var ticks = rest.Ticks * Math.Pow(RestGrowth, strikes);
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>How long a block rests the item for the site; null for <see cref="BlockPolicy.Ignore"/>, MaxValue for Remove.</summary>
    public TimeSpan? BlockRest => BlockPolicy switch
    {
        BlockPolicy.Block10Minutes => TimeSpan.FromMinutes(10),
        BlockPolicy.Block1Hour => TimeSpan.FromHours(1),
        BlockPolicy.Block1Day => TimeSpan.FromDays(1),
        BlockPolicy.Remove => TimeSpan.MaxValue,
        _ => null,
    };
}

/// <summary>
/// What a list knows of one of its items (a proxy, a user agent): its counters, its rests for every site and for a site, its errors in a
/// row, the sessions open on it. Used under the lock of its list.
/// </summary>
internal sealed class Health
{
    public Dictionary<string, DateTimeOffset> BlockedSites { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (int Count, DateTimeOffset At)> BadResponsesInARow { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Rests for a site (blocks, bad responses in a row) not forgiven yet, and the good responses in a row from it since the last error.</summary>
    public Dictionary<string, (int Strikes, int GoodInARow)> SiteStrikes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Rests for every site (failures in a row) not forgiven yet, and the successes in a row since the last failure.</summary>
    public int DeadStrikes, SuccessesInARow;
    public DateTimeOffset DeadUntil, LastFailureAt;
    public int FailuresInARow, Sessions;
    public bool Listed = true;
    public long Loads, Successes, Failures, Blocks, BadResponses;

    public DateTimeOffset BlockedUntil(string site) => BlockedSites.TryGetValue(site, out var until) ? until : DateTimeOffset.MinValue;

    public int CountBlockedSites(DateTimeOffset now) => BlockedSites.Count(b => b.Value > now);

    /// <summary>Rests for every site or for this one.</summary>
    public bool Rests(string site, DateTimeOffset now) => DeadUntil > now || BlockedUntil(site) > now;

    /// <summary>When the item may load from the site again.</summary>
    public DateTimeOffset BackAt(string site) => DeadUntil > BlockedUntil(site) ? DeadUntil : BlockedUntil(site);

    /// <summary>
    /// A failure or a bad response for the site not long ago: the item goes after the clean ones. Successes don't put an item ahead (the
    /// turn stays even); errors put it back. An error older than its pause is forgotten.
    /// </summary>
    public bool IsPessimized(string site, DateTimeOffset now, RestRules rules)
    {
        if (FailuresInARow > 0)
        {
            if (now < LastFailureAt + rules.FailurePause) return true;
            FailuresInARow = 0;
        }
        if (BadResponsesInARow.TryGetValue(site, out var bad))
        {
            if (now < bad.At + rules.BadResponsePause) return true;
            BadResponsesInARow.Remove(site);
        }
        return false;
    }

    /// <summary>An answer came: the failures in a row start again.</summary>
    /// <summary>
    /// An answer came: the failures in a row start again. One success does not forgive a rest: <see cref="RestRules.GoodInARowToForgive"/>
    /// in a row forgive one.
    /// </summary>
    public void Success()
    {
        Successes++;
        FailuresInARow = 0;
        if (DeadStrikes > 0 && ++SuccessesInARow >= RestRules.GoodInARowToForgive)
        {
            DeadStrikes--;
            SuccessesInARow = 0;
        }
    }

    /// <summary>No answer: the rest for every site this failure starts (longer for every rest before it), null for none.</summary>
    public TimeSpan? Failure(DateTimeOffset now, RestRules rules)
    {
        Failures++;
        LastFailureAt = now;
        SuccessesInARow = 0;
        if (++FailuresInARow < rules.MaxConsecutiveFailures) return null;
        FailuresInARow = 0;
        var rest = rules.Grown(rules.FailurePause, DeadStrikes++);
        DeadUntil = now + rest;
        return rest;
    }

    /// <summary>A site blocked the answer: the rest for the site (longer for every rest for it before), null for none.</summary>
    public TimeSpan? Blocked(string site, DateTimeOffset now, RestRules rules)
    {
        Blocks++;
        if (rules.BlockRest is not { } policyRest) return null;
        var rest = rules.Grown(policyRest, Strike(site));
        BlockedSites[site] = rest == TimeSpan.MaxValue ? DateTimeOffset.MaxValue : now + rest;
        return rest;
    }

    /// <summary>
    /// The answer was not the page: the rest for the site when bad responses in a row start one (longer for every rest for it before),
    /// null for none.
    /// </summary>
    public TimeSpan? BadResponse(string site, DateTimeOffset now, RestRules rules)
    {
        BadResponses++;
        if (SiteStrikes.TryGetValue(site, out var strikes)) SiteStrikes[site] = (strikes.Strikes, 0);
        var inARow = (BadResponsesInARow.TryGetValue(site, out var bad) && now < bad.At + rules.BadResponsePause ? bad.Count : 0) + 1;
        if (inARow < rules.MaxConsecutiveBadResponses)
        {
            BadResponsesInARow[site] = (inARow, now);
            return null;
        }
        BadResponsesInARow.Remove(site);
        var rest = rules.Grown(rules.BadResponsePause, Strike(site));
        var until = now + rest;
        if (BlockedUntil(site) < until) BlockedSites[site] = until;     // a longer block stays
        return rest;
    }

    /// <summary>
    /// The answer was the page: the bad responses in a row for the site start again; <see cref="RestRules.GoodInARowToForgive"/> in a row
    /// forgive a rest for it.
    /// </summary>
    public void GoodResponse(string site)
    {
        BadResponsesInARow.Remove(site);
        if (!SiteStrikes.TryGetValue(site, out var strikes)) return;
        var good = strikes.GoodInARow + 1;
        if (good < RestRules.GoodInARowToForgive) SiteStrikes[site] = (strikes.Strikes, good);
        else if (strikes.Strikes > 1) SiteStrikes[site] = (strikes.Strikes - 1, 0);
        else SiteStrikes.Remove(site);
    }

    /// <summary>The rests for the site before this one; counts this one.</summary>
    private int Strike(string site)
    {
        var before = SiteStrikes.TryGetValue(site, out var strikes) ? strikes.Strikes : 0;
        SiteStrikes[site] = (before + 1, 0);
        return before;
    }

    /// <summary>
    /// The item to give out for <paramref name="site"/>, from <paramref name="cursor"/> on: clean and with no session first; then clean
    /// with sessions (fewest first), pessimized with none, pessimized with sessions. -1 when every item rests for the site.
    /// </summary>
    public static int Pick<T>(IReadOnlyList<T> items, Func<T, Health> health, int cursor, string site, DateTimeOffset now, RestRules rules)
    {
        int best = -1, bestRank = int.MaxValue, bestSessions = int.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            var index = (cursor + i) % items.Count;
            var item = health(items[index]);
            if (item.Rests(site, now)) continue;
            var rank = (item.IsPessimized(site, now, rules) ? 2 : 0) + (item.Sessions > 0 ? 1 : 0);
            if (rank == 0) return index;
            if (rank < bestRank || (rank == bestRank && item.Sessions < bestSessions))
            {
                (best, bestRank, bestSessions) = (index, rank, item.Sessions);
            }
        }
        return best;
    }
}
