using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Email.Templates;

/// <summary>
/// Every absolute URL an email contains. Site links carry UTM parameters so a visit can be
/// attributed to the campaign and step; preference links carry their token in the fragment,
/// which browsers never send to a server, so it stays out of logs and Referer headers.
/// </summary>
public sealed class EmailLinkBuilder(IOptions<EmailOptions> options)
{
    private readonly string _web = options.Value.WebBaseUrl.TrimEnd('/');
    private readonly string _api = options.Value.ApiBaseUrl.TrimEnd('/');

    /// <param name="path">Site-relative, may carry its own query and fragment.</param>
    /// <param name="campaign">The campaign name (utm_campaign).</param>
    /// <param name="content">The step or period (utm_content), e.g. "step-2" or "2026-11".</param>
    public string Web(string path, string campaign, string content)
        => QueryHelpers.AddQueryString(_web + "/" + path.TrimStart('/'), new Dictionary<string, string?>
        {
            ["utm_source"] = "email",
            ["utm_medium"] = "email",
            ["utm_campaign"] = campaign,
            ["utm_content"] = content,
        });

    public string Home() => _web + "/";

    public string UnsubscribePage(string unsubscribeToken) => $"{_web}/email-preferences#u={unsubscribeToken}";

    public string ManagePage(string manageToken) => $"{_web}/email-preferences#m={manageToken}";

    /// <summary>The RFC 8058 one-click target mail clients POST to. Tokens are URL-safe as issued.</summary>
    public string OneClick(string unsubscribeToken) => $"{_api}/api/email/unsubscribe?t={unsubscribeToken}";
}
