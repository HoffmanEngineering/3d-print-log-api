using System.Text;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Templates.PrinterSilent;

namespace PrintLogApi.Email.Campaigns;

/// <param name="MakeModel">Shown under the name when the printer has both a name and a make/model.</param>
/// <param name="SourceLabel">How it reported, e.g. "Moonraker" or "the slicer uploader".</param>
/// <param name="LastHeard">The last automated print's date in the user's zone, e.g. "Oct 2".</param>
/// <param name="QuietDays">Whole days from that print to when the email was due.</param>
public sealed record SilentPrinterView(
    string Name, string? MakeModel, string SourceLabel, string LastHeard, string TroubleshootUrl, string TroubleshootLabel, string PrinterUrl, int QuietDays);

/// <param name="ApiKeyLastUsed">When set, the user's newest API key was last used before the silence began.</param>
public sealed record PrinterSilentModel(string? Name, IReadOnlyList<SilentPrinterView> Printers, string? ApiKeyLastUsed);

public static class PrinterSilentTemplates
{
    public static string Subject(PrinterSilentModel model) => model.Printers.Count == 1
        ? $"Did your {model.Printers[0].Name} stop reporting?"
        : $"{model.Printers.Count} of your printers stopped reporting";

    public static async Task<(string Html, string Text)> RenderAsync(IEmailTemplateRenderer renderer, PrinterSilentModel model, EmailFooterModel footer)
    {
        var html = await renderer.RenderAsync<PrinterSilentEmail>(new Dictionary<string, object?> { ["Model"] = model, ["Footer"] = footer });
        return (html, Text(model, footer));
    }

    public static string Intro(PrinterSilentModel model) => model.Printers.Count == 1
        ? "Your printer usually logs its prints on its own, but we haven't heard from it in a while. If it's still printing, the connection probably needs a look."
        : "These printers usually log their prints on their own, but we haven't heard from them in a while. If they're still printing, the connections probably need a look.";

    /// <summary>The pill on each printer card, e.g. "Quiet 16 days".</summary>
    public static string QuietLabel(int days) => days == 1 ? "Quiet 1 day" : $"Quiet {days} days";

    public static string ApiKeyLine(string lastUsed)
        => $"Your newest API key was last used on {lastUsed}. If you replaced or deleted a key, update the one saved in your printer's config.";

    public static string Text(PrinterSilentModel m, EmailFooterModel footer)
    {
        var b = new StringBuilder();
        b.Append(Subject(m)).Append("\n\n");
        b.Append(EmailFormat.Greeting(m.Name)).Append("\n\n");
        b.Append(Intro(m)).Append("\n\n");

        foreach (var p in m.Printers)
        {
            b.Append(p.Name);
            if (p.MakeModel is not null)
            {
                b.Append(" (").Append(p.MakeModel).Append(')');
            }

            b.Append('\n');
            b.Append(QuietLabel(p.QuietDays)).Append(". ");
            b.Append("Last heard from via ").Append(p.SourceLabel).Append(" on ").Append(p.LastHeard).Append('\n');
            b.Append(p.TroubleshootLabel).Append(": ").Append(p.TroubleshootUrl).Append('\n');
            b.Append("Not using this printer anymore? Mark it inactive: ").Append(p.PrinterUrl).Append("\n\n");
        }

        if (m.ApiKeyLastUsed is { } used)
        {
            b.Append(ApiKeyLine(used)).Append('\n');
        }

        return b.Append(OnboardingTemplates.TextFooter(footer)).ToString();
    }
}
