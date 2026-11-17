namespace PrintLogApi.Services.Spoolman;

/// <summary>
/// The tiers open Spoolman sockets (#178) are counted by, as the <c>tier</c> dimension of the
/// <c>Spoolman_OpenSockets</c> Application Insights metric (<see cref="SpoolmanSocketSampler"/>).
/// Tier and nothing else: a user or connection dimension would be unbounded cardinality. B1 allows
/// 350 sockets per instance; plan the move to S1 or P0v3 at about 250 (see the Usage tab of the
/// monitoring workbook).
/// </summary>
public static class SpoolmanSocketMetrics
{
    public const string Free = "free";
    public const string Pro = "pro";
}
