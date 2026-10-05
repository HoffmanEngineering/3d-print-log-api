using Microsoft.Extensions.Options;
using PrintLogApi.Email.Tokens;

namespace PrintLogApi.Email.Templates;

/// <param name="ReasonLine">Why this person gets this email, e.g. "...and monthly recaps are on."</param>
public record EmailFooterModel(string ReasonLine, string ManageUrl, string UnsubscribeUrl, string PostalAddress, string HomeUrl);

public interface IEmailFooterFactory
{
    /// <summary>
    /// The footer every campaign email carries, and the matching RFC 8058 headers. The footer's
    /// unsubscribe link and the one-click header switch off the same category.
    /// </summary>
    /// <param name="unsubscribeCategory">The <see cref="EmailSettingTypes"/> id to switch off (22 = all).</param>
    (EmailFooterModel Footer, IReadOnlyDictionary<string, string> Headers) Create(long userId, int unsubscribeCategory, string reasonLine);
}

public sealed class EmailFooterFactory(EmailLinkBuilder links, IEmailTokenService tokens, IOptions<EmailOptions> options) : IEmailFooterFactory
{
    public (EmailFooterModel Footer, IReadOnlyDictionary<string, string> Headers) Create(long userId, int unsubscribeCategory, string reasonLine)
    {
        var unsubscribe = tokens.CreateUnsubscribe(userId, unsubscribeCategory);

        var footer = new EmailFooterModel(
            reasonLine,
            links.ManagePage(tokens.CreateManage(userId)),
            links.UnsubscribePage(unsubscribe),
            options.Value.PostalAddress,
            links.Home());

        var headers = new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = $"<{links.OneClick(unsubscribe)}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        };

        return (footer, headers);
    }
}
