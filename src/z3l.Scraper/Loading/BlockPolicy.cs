namespace z3l.Scraper.Loading;

/// <summary>
/// What happens to a proxy (<see cref="ProxyList.BlockPolicy"/>) or a user agent (<see cref="UserAgentList.BlockPolicy"/>) when a
/// site blocks it. The block is for that site only: the other sites keep using it.
/// </summary>
public enum BlockPolicy
{
    /// <summary>Blocks are counted, it keeps working.</summary>
    Ignore,
    /// <summary>It rests 10 minutes for that site.</summary>
    Block10Minutes,
    /// <summary>It rests an hour for that site. The default.</summary>
    Block1Hour,
    /// <summary>It rests a day for that site.</summary>
    Block1Day,
    /// <summary>It is not used for that site again while the list lives.</summary>
    Remove,
}
