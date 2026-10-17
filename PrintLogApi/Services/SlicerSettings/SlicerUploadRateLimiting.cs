using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace PrintLogApi.Services.SlicerSettings;

/// <summary>
/// The anonymous budget on <c>POST /api/Slicer/uploads</c>: per client address, a per-minute and a
/// per-day fixed window, and a request needs a permit from both. Read from
/// <c>Slicer:UploadRateLimitPerMinute</c> (10) and <c>Slicer:UploadRateLimitPerDay</c> (200); 0 or
/// less turns that window off, which is how the integration suite opts out.
/// <para>
/// The address is the socket peer, as for every other anonymous budget here: the pipeline does not
/// process forwarded headers, and a client-supplied <c>X-Forwarded-For</c> would let one caller
/// rotate partitions. If the App Service front end turns out to collapse callers into one peer, all
/// plugin users share this budget, and these limits must be raised (or the front end's client
/// address trusted) before the plugin ships. IPv6 callers are partitioned by their /64, which one
/// host can otherwise rotate through freely.
/// </para>
/// </summary>
public static class SlicerUploadRateLimiting
{
    public const string Policy = "slicer-uploads";

    public static RateLimitPartition<string> GetPartition(HttpContext httpContext, IConfiguration configuration)
    {
        var perMinute = configuration.GetValue("Slicer:UploadRateLimitPerMinute", 10);
        var perDay = configuration.GetValue("Slicer:UploadRateLimitPerDay", 200);
        if (perMinute <= 0 && perDay <= 0)
        {
            return RateLimitPartition.GetNoLimiter("slicer-uploads-unlimited");
        }

        return RateLimitPartition.Get($"slicer-uploads:{PartitionKey(httpContext.Connection.RemoteIpAddress)}", _ =>
        {
            var windows = new List<RateLimiter>();
            if (perMinute > 0)
            {
                windows.Add(Window(perMinute, TimeSpan.FromMinutes(1)));
            }

            if (perDay > 0)
            {
                windows.Add(Window(perDay, TimeSpan.FromDays(1)));
            }

            return windows.Count == 1 ? windows[0] : RateLimiter.CreateChained([.. windows]);
        });
    }

    /// <summary>An IPv4 address as is (IPv4-mapped IPv6 included); an IPv6 address by its /64.</summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static FixedWindowRateLimiter Window(int permits, TimeSpan window) => new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = permits,
        Window = window,
        QueueLimit = 0,
    });
}
