using System.Text;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Templates.DevicePaired;

namespace PrintLogApi.Email.Campaigns;

/// <param name="Name">The account's display name, for the greeting.</param>
/// <param name="DeviceName">What the device calls itself. The device chose it, so it is only ever shown as encoded text, never in the subject.</param>
/// <param name="Repaired">A device already paired was paired again, rather than a new one.</param>
/// <param name="When">When it paired, in the user's zone with the zone named, or labeled UTC.</param>
/// <param name="Permissions">What it may do, in plain words (<c>DeviceScopes.Describe</c>).</param>
/// <param name="ReviewUrl">Settings, at Connected Agents, where the user revokes it.</param>
/// <param name="IpAddress">The address the pairing request came from, as the API saw it; null when unknown. Never in the subject.</param>
public sealed record DevicePairedModel(string? Name, string DeviceName, bool Repaired, string When, string Permissions, string ReviewUrl, string? IpAddress = null);

public static class DevicePairedTemplates
{
    /// <summary>Fixed text: a device name in a subject line would let whoever paired it write the inbox preview.</summary>
    public static string Subject(DevicePairedModel model) => model.Repaired
        ? "A device was paired again with your 3D Print Log account"
        : "A new device was paired with your 3D Print Log account";

    public static string Intro(DevicePairedModel model) => model.Repaired
        ? "A device already paired with your account was paired again and received a new token. It can keep logging to your account."
        : "A device was just paired with your account. It can now log to your account on its own.";

    public const string IfYou = "If this was you, there's nothing to do.";

    public const string IfNotYou =
        "If you don't recognize it, revoke it now in Settings, under Connected Agents. Revoking stops it on its next request.";

    public const string ButtonLabel = "Review paired devices";

    public static async Task<(string Html, string Text)> RenderAsync(IEmailTemplateRenderer renderer, DevicePairedModel model, EmailFooterModel footer)
    {
        var html = await renderer.RenderAsync<DevicePairedEmail>(new Dictionary<string, object?> { ["Model"] = model, ["Footer"] = footer });
        return (html, Text(model, footer));
    }

    public static string Text(DevicePairedModel m, EmailFooterModel footer)
    {
        var b = new StringBuilder();
        b.Append(Subject(m)).Append("\n\n");
        b.Append(EmailFormat.Greeting(m.Name)).Append("\n\n");
        b.Append(Intro(m)).Append("\n\n");
        b.Append("Device: ").Append(m.DeviceName).Append('\n');
        b.Append("Paired: ").Append(m.When).Append('\n');
        if (m.IpAddress is { } ip)
        {
            b.Append("From IP address: ").Append(ip).Append('\n');
        }

        b.Append("It can: ").Append(m.Permissions).Append("\n\n");
        b.Append(IfYou).Append('\n');
        b.Append(IfNotYou).Append('\n');
        b.Append(ButtonLabel).Append(": ").Append(m.ReviewUrl).Append('\n');
        return b.Append(OnboardingTemplates.TextFooter(footer)).ToString();
    }
}
