using System.Text;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Templates.Onboarding;

namespace PrintLogApi.Email.Campaigns;

/// <summary>Everything an onboarding email shows. Built by the campaign; rendered by the templates.</summary>
/// <param name="Step">welcome, connect, first-print or next-steps.</param>
/// <param name="Name">The display name to greet, if any.</param>
/// <param name="HintText">The next badge's instruction, e.g. "Add your first printer."</param>
public sealed record OnboardingModel(
    string Step,
    string? Name,
    string CtaUrl,
    string CtaLabel,
    string HintText,
    int PrintCount,
    double PrintHours,
    double FilamentGrams,
    int GettingStartedHeld,
    int GettingStartedTotal,
    string OctoPrintUrl,
    string KlipperUrl,
    string SlicerUrl,
    string FirstPrintUrl);

/// <summary>Subjects, preheaders, HTML (Razor) and plain text for each onboarding step.</summary>
public static class OnboardingTemplates
{
    public const string Welcome = "welcome";
    public const string Connect = "connect";
    public const string FirstPrint = "first-print";
    public const string NextSteps = "next-steps";

    public static readonly IReadOnlyList<string> Steps = [Welcome, Connect, FirstPrint, NextSteps];

    public static string Subject(string step) => step switch
    {
        Welcome => "Welcome to 3D Print Log",
        Connect => "Let your prints log themselves",
        FirstPrint => "Log your first print in 30 seconds",
        NextSteps => "Your first prints, and what's next",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    public static async Task<(string Html, string Text)> RenderAsync(IEmailTemplateRenderer renderer, OnboardingModel model, EmailFooterModel footer)
    {
        var parameters = new Dictionary<string, object?> { ["Model"] = model, ["Footer"] = footer };
        var html = model.Step switch
        {
            Welcome => await renderer.RenderAsync<WelcomeEmail>(parameters),
            Connect => await renderer.RenderAsync<ConnectEmail>(parameters),
            FirstPrint => await renderer.RenderAsync<FirstPrintEmail>(parameters),
            NextSteps => await renderer.RenderAsync<NextStepsEmail>(parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(model), model.Step, null),
        };

        return (html, Text(model, footer));
    }

    /// <summary>The plain-text part, written from the same model rather than stripped from the HTML.</summary>
    public static string Text(OnboardingModel m, EmailFooterModel footer)
    {
        var b = new StringBuilder();
        b.Append(EmailFormat.Greeting(m.Name)).Append("\n\n");

        switch (m.Step)
        {
            case Welcome:
                b.Append("Thanks for signing up. 3D Print Log keeps a record of every print: what you printed, on which printer, with which spool, how long it took and how it turned out. Over time that record answers real questions, like which settings work and how much filament you have left.\n\n");
                b.Append("Your next step: ").Append(m.HintText).Append('\n');
                b.Append(m.CtaLabel).Append(": ").Append(m.CtaUrl).Append("\n\n");
                b.Append("Questions or ideas? Just reply to this email.\n");
                break;
            case Connect:
                b.Append("Typing in every print gets old fast. Connect the tool you already use and new prints show up on their own:\n\n");
                b.Append("- OctoPrint: install the webhook plugin. ").Append(m.OctoPrintUrl).Append('\n');
                b.Append("- Klipper (Moonraker): add one block to your config. ").Append(m.KlipperUrl).Append('\n');
                b.Append("- Your slicer: upload straight from PrusaSlicer, OrcaSlicer or SuperSlicer. ").Append(m.SlicerUrl).Append("\n\n");
                b.Append("Each one takes about five minutes.\n");
                break;
            case FirstPrint:
                b.Append("You haven't logged a print yet. The quickest way: drop a G-code file on the new print page, and 3D Print Log reads the print time, filament and slicer settings for you.\n\n");
                b.Append("Log a print: ").Append(m.FirstPrintUrl).Append("\n\n");
                b.Append("No file handy? You can type the details in on the same page.\n");
                break;
            case NextSteps:
                b.Append("Here's where you are so far:\n\n");
                b.Append("- ").Append(EmailFormat.Count(m.PrintCount, "print", "prints")).Append(" logged\n");
                b.Append("- ").Append(EmailFormat.Hours(m.PrintHours)).Append(" of print time\n");
                b.Append("- ").Append(EmailFormat.Weight(m.FilamentGrams)).Append(" of filament\n");
                b.Append("- ").Append(m.GettingStartedHeld).Append(" of ").Append(m.GettingStartedTotal).Append(" Getting started badges\n\n");
                b.Append("Next: ").Append(m.HintText).Append('\n');
                b.Append(m.CtaLabel).Append(": ").Append(m.CtaUrl).Append('\n');
                break;
        }

        return b.Append(TextFooter(footer)).ToString();
    }

    internal static string TextFooter(EmailFooterModel footer) => $"""

        --
        {footer.ReasonLine}
        Manage email preferences: {footer.ManageUrl}
        Unsubscribe: {footer.UnsubscribeUrl}
        {footer.PostalAddress}

        """.Replace("\r\n", "\n");
}
