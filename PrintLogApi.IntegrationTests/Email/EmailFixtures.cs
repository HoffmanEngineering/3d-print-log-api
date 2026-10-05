using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Templates;
using PrintLogApi.IntegrationTests.Email.Campaigns;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>
/// Every campaign email rendered from the golden fixtures, with what a reader must be able to see
/// in each even with images blocked. The HTML rules and the preview writer iterate this one set.
/// </summary>
internal static class EmailFixtures
{
    internal sealed record Fixture(string Name, Func<IServiceProvider, Task<(string Html, string Text)>> Render, IReadOnlyList<string> MustShow);

    public static IReadOnlyList<Fixture> All { get; } = Build();

    public static IEnumerable<string> Names => All.Select(f => f.Name);

    public static Fixture Get(string name) => All.Single(f => f.Name == name);

    private static IReadOnlyList<Fixture> Build()
    {
        var list = new List<Fixture>();

        void Recap(string name, Func<IServiceProvider, MonthlyRecapModel> model, EmailFooterModel footer)
            => list.Add(new Fixture(
                name,
                sp => MonthlyRecapTemplates.RenderAsync(sp.GetRequiredService<IEmailTemplateRenderer>(), model(sp), footer),
                [.. RecapShows(model)]));

        Recap("monthly-recap", _ => MonthlyRecapCampaignTests.GoldenModel(), CampaignTestData.Footer);
        Recap("monthly-recap-stress", sp => MonthlyRecapCampaignTests.StressModel(sp.GetRequiredService<EmailAssets>()), MonthlyRecapCampaignTests.AskFooter);
        Recap("monthly-recap-sparse", _ => MonthlyRecapCampaignTests.SparseModel(), CampaignTestData.Footer);

        foreach (var step in OnboardingTemplates.Steps)
        {
            var model = OnboardingCampaignTests.GoldenModel(step);
            list.Add(new Fixture(
                $"onboarding-{step}",
                sp => OnboardingTemplates.RenderAsync(sp.GetRequiredService<IEmailTemplateRenderer>(), model, CampaignTestData.Footer),
                [OnboardingTemplates.Subject(step)]));
        }

        var printers = PrinterSilentCampaignTests.GoldenModel();
        list.Add(new Fixture(
            "printer-silent",
            sp => PrinterSilentTemplates.RenderAsync(sp.GetRequiredService<IEmailTemplateRenderer>(), printers, CampaignTestData.Footer),
            [PrinterSilentTemplates.Subject(printers), .. printers.Printers.Select(p => p.Name)]));

        return list;
    }

    // The headline, every number and every shown badge title; the model is only built to read
    // them, and the stress model's badges don't depend on the base URL.
    private static IEnumerable<string> RecapShows(Func<IServiceProvider, MonthlyRecapModel> model)
    {
        var m = model(Services.Instance);
        yield return MonthlyRecapTemplates.Headline(m);
        foreach (var stat in MonthlyRecapTemplates.Stats(m))
        {
            yield return stat.Value;
        }

        foreach (var badge in m.Badges)
        {
            yield return badge.Title;
        }
    }

    private sealed class Services : IServiceProvider
    {
        public static readonly Services Instance = new();

        private static readonly EmailAssets Assets = new(Microsoft.Extensions.Options.Options.Create(
            new PrintLogApi.Email.EmailOptions { WebBaseUrl = "https://www.3dprintlog.test" }));

        public object? GetService(Type serviceType) => serviceType == typeof(EmailAssets) ? Assets : null;
    }
}
