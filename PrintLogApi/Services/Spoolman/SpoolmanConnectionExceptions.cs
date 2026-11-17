namespace PrintLogApi.Services.Spoolman;

/// <summary>Free allows <see cref="Limit"/> active Spoolman connection(s), and the user has that many.</summary>
public sealed class SpoolmanConnectionLimitException(int limit)
    : Exception($"The Free plan includes {limit} Spoolman-connected printer. Disconnect it, or upgrade to Pro for more.")
{
    public int Limit { get; } = limit;
}
