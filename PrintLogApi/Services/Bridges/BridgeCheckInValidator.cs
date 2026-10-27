using System.Globalization;
using System.Text.RegularExpressions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Bridges;

namespace PrintLogApi.Services.Bridges;

/// <summary>One validated discovered printer, ready to store.</summary>
public sealed record ValidDiscovery(
    string IdentityKey,
    string Kind,
    string Address,
    int? Port,
    string? Model,
    string? Name,
    string? Serial,
    string? MainboardId,
    IReadOnlyList<string> Macs,
    bool Configured);

/// <summary>A validated check-in. A null field or list keeps what is stored.</summary>
public sealed record ValidCheckIn(
    string? AgentVersion,
    string? Mode,
    string? HostName,
    IReadOnlyList<string>? Networks,
    IReadOnlyList<ValidDiscovery>? Discovered,
    DateTimeOffset? LastSweepAt,
    IReadOnlyList<RejectedEntryDto> Rejected);

/// <summary>
/// Checks a check-in (printlog-bridge#100). The report as a whole is refused (a 400) when it is
/// over a cap or a scalar the bridge fully controls is malformed: those are bugs. A list entry
/// that breaks a rule is dropped and named in <see cref="ValidCheckIn.Rejected"/>, because one
/// odd printer on a LAN must not lock the bridge out of its desired printers.
/// </summary>
public static partial class BridgeCheckInValidator
{
    public static (ValidCheckIn? CheckIn, string? Error) Validate(BridgeCheckInDto dto)
    {
        if (dto.Networks is { Count: > Bridge.MaxNetworks })
        {
            return (null, $"networks: at most {Bridge.MaxNetworks}.");
        }

        if (dto.Discovered is { Count: > Bridge.MaxDiscoveredPrinters })
        {
            return (null, $"discovered: at most {Bridge.MaxDiscoveredPrinters}.");
        }

        if (dto.Discovered?.Any(d => d?.Macs is { Count: > BridgeRules.MaxMacs }) == true)
        {
            return (null, $"discovered[].macs: at most {BridgeRules.MaxMacs}.");
        }

        var agentVersion = Blank(dto.AgentVersion);
        if (agentVersion is not null && !AgentVersionShape().IsMatch(agentVersion))
        {
            return (null, $"agentVersion: up to {Bridge.AgentVersionMaxLength} of letters, digits and . + -.");
        }

        var mode = Blank(dto.Mode);
        if (mode is not null && !ModeShape().IsMatch(mode))
        {
            return (null, $"mode: lower-case letters, digits and dashes, up to {Bridge.ModeMaxLength}.");
        }

        if (!BridgeRules.TryText(dto.HostName, out var hostName, Bridge.HostNameMaxLength))
        {
            return (null, $"hostName: up to {Bridge.HostNameMaxLength} characters, no control characters.");
        }

        var rejected = new List<RejectedEntryDto>();
        return (new ValidCheckIn(agentVersion, mode, hostName, Networks(dto.Networks, rejected), Discovered(dto.Discovered, rejected), dto.LastSweepAt, rejected), null);
    }

    private static List<string>? Networks(List<string?>? networks, List<RejectedEntryDto> rejected)
    {
        if (networks is null)
        {
            return null;
        }

        var valid = new List<string>();
        for (var i = 0; i < networks.Count; i++)
        {
            var (network, code) = BridgeRules.Network(networks[i]?.Trim());
            if (network is null)
            {
                rejected.Add(Reject("networks", i, code!));
            }
            else if (!valid.Contains(network, StringComparer.Ordinal))
            {
                valid.Add(network);
            }
        }

        return valid;
    }

    private static List<ValidDiscovery>? Discovered(List<DiscoveredPrinterReportDto?>? discovered, List<RejectedEntryDto> rejected)
    {
        if (discovered is null)
        {
            return null;
        }

        var valid = new List<ValidDiscovery>();
        for (var i = 0; i < discovered.Count; i++)
        {
            var (printer, code) = One(discovered[i]);
            if (printer is not null && valid.Any(v => v.IdentityKey == printer.IdentityKey))
            {
                (printer, code) = (null, "duplicate_printer");
            }

            if (printer is null)
            {
                rejected.Add(Reject("discovered", i, code!));
            }
            else
            {
                valid.Add(printer);
            }
        }

        return valid;
    }

    private static (ValidDiscovery?, string?) One(DiscoveredPrinterReportDto? d)
    {
        if (d is null)
        {
            return (null, "invalid_kind");
        }

        if (BridgeRules.Kind(d.Kind) is not { } kind)
        {
            return (null, "invalid_kind");
        }

        var (address, addressCode) = BridgeRules.Address(d.Address?.Trim());
        if (address is null)
        {
            return (null, addressCode);
        }

        if (!BridgeRules.IsPort(d.Port))
        {
            return (null, "invalid_port");
        }

        if (!BridgeRules.TryText(d.Model, out var model) || !BridgeRules.TryText(d.Name, out var name))
        {
            return (null, "invalid_text");
        }

        var serial = Blank(d.Serial);
        if (serial is not null && !BridgeRules.IsId(serial))
        {
            return (null, "invalid_serial");
        }

        var mainboardId = Blank(d.MainboardId);
        if (mainboardId is not null && !BridgeRules.IsId(mainboardId))
        {
            return (null, "invalid_mainboard_id");
        }

        var macs = new List<string>();
        foreach (var text in d.Macs ?? [])
        {
            if (BridgeRules.Mac(text?.Trim()) is not { } mac)
            {
                return (null, "invalid_mac");
            }

            if (!macs.Contains(mac, StringComparer.Ordinal))
            {
                macs.Add(mac);
            }
        }

        var key = BridgeRules.IdentityKey(kind, serial, mainboardId, macs, address, d.Port);
        return (new ValidDiscovery(key, kind, address, d.Port, model, name, serial, mainboardId, macs, d.Configured ?? false), null);
    }

    private static RejectedEntryDto Reject(string list, int index, string code)
        => new() { Field = $"{list}[{index.ToString(CultureInfo.InvariantCulture)}]", Code = code };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^[0-9A-Za-z.+-]{1,50}$")]
    private static partial Regex AgentVersionShape();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,19}$")]
    private static partial Regex ModeShape();
}
