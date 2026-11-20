namespace z3l.Scraper.Loading;

/// <summary>How a list rests what it gives out (a proxy, a user agent): its block policy, failures and bad responses in a row, pauses.</summary>
internal readonly record struct RestRules(ProxyBlockPolicy BlockPolicy, int MaxConsecutiveFailures, TimeSpan FailurePause,
    int MaxConsecutiveBadResponses, TimeSpan BadResponsePause)
{
    /// <summary>How long a block rests the item for the site; null for <see cref="ProxyBlockPolicy.Ignore"/>, MaxValue for Remove.</summary>
    public TimeSpan? BlockRest => BlockPolicy switch
    {
        ProxyBlockPolicy.Block10Minutes => TimeSpan.FromMinutes(10),
        ProxyBlockPolicy.Block1Hour => TimeSpan.FromHours(1),
        ProxyBlockPolicy.Block1Day => TimeSpan.FromDays(1),
        ProxyBlockPolicy.Remove => TimeSpan.MaxValue,
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
    public void Success()
    {
        Successes++;
        FailuresInARow = 0;
    }

    /// <summary>No answer: true when this failure starts a rest for every site.</summary>
    public bool Failure(DateTimeOffset now, RestRules rules)
    {
        Failures++;
        LastFailureAt = now;
        if (++FailuresInARow < rules.MaxConsecutiveFailures) return false;
        FailuresInARow = 0;
        DeadUntil = now + rules.FailurePause;
        return true;
    }

    /// <summary>A site blocked the answer: true when the item now rests for the site.</summary>
    public bool Blocked(string site, DateTimeOffset now, RestRules rules)
    {
        Blocks++;
        if (rules.BlockRest is not { } rest) return false;
        BlockedSites[site] = rest == TimeSpan.MaxValue ? DateTimeOffset.MaxValue : now + rest;
        return true;
    }

    /// <summary>The answer was not the page: true when bad responses in a row now rest the item for the site.</summary>
    public bool BadResponse(string site, DateTimeOffset now, RestRules rules)
    {
        BadResponses++;
        var inARow = (BadResponsesInARow.TryGetValue(site, out var bad) && now < bad.At + rules.BadResponsePause ? bad.Count : 0) + 1;
        if (inARow < rules.MaxConsecutiveBadResponses)
        {
            BadResponsesInARow[site] = (inARow, now);
            return false;
        }
        BadResponsesInARow.Remove(site);
        var until = now + rules.BadResponsePause;
        if (BlockedUntil(site) < until) BlockedSites[site] = until;     // a longer block stays
        return true;
    }

    /// <summary>The answer was the page: the bad responses in a row for the site start again.</summary>
    public void GoodResponse(string site) => BadResponsesInARow.Remove(site);

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
