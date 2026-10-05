using PrintLogApi.Models.Smtp;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class SmtpEmailSenderTests
{
    private static readonly SmtpEmailSenderOptions Options = new() { SenderEmail = "feedback@3dprintlog.com", SenderName = "3D Print Log" };

    [Fact]
    public void BuildMessage_TextPartHasNoMarkup()
    {
        var message = SmtpEmailSender.BuildMessage(Options, "hello@3dprintlog.com", "Feedback", "<p>Great app &amp; thanks</p><br/><b>Bold</b>");

        var text = message.GetTextBody(MimeKit.Text.TextFormat.Plain);
        Assert.DoesNotContain("<", text);
        Assert.Contains("Great app & thanks", text);
        Assert.Contains("Bold", text);
    }

    [Fact]
    public void BuildMessage_KeepsHtmlPart()
    {
        var message = SmtpEmailSender.BuildMessage(Options, "hello@3dprintlog.com", "Feedback", "<p>Hi</p>");

        Assert.Equal("<p>Hi</p>", message.HtmlBody);
    }
}
