using PrintLogApi.Email;
using PrintLogApi.Email.Transport;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class SesEmailTransportRequestTests
{
    private static readonly EmailMessage Message = new(
        ToAddress: "maker@example.com",
        Subject: "Your November in prints",
        Html: "<p>Hi</p>",
        Text: "Hi",
        Headers: new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = "<https://api.example.com/api/email/unsubscribe?t=abc>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        },
        Campaign: "monthly-recap",
        OutboxId: 4242);

    private static readonly EmailOptions Options = new() { Ses = new SesOptions { ConfigurationSet = "printlog-email" } };

    [Fact]
    public void BuildRequest_SetsSenderAndReplyTo()
    {
        var request = SesEmailTransport.BuildRequest(Message, Options);

        Assert.Equal("3D Print Log <updates@mail.3dprintlog.com>", request.FromEmailAddress);
        Assert.Equal(["hello@3dprintlog.com"], request.ReplyToAddresses);
        Assert.Equal(["maker@example.com"], request.Destination.ToAddresses);
        Assert.Equal("printlog-email", request.ConfigurationSetName);
    }

    [Fact]
    public void BuildRequest_CarriesUnsubscribeHeaders()
    {
        var headers = SesEmailTransport.BuildRequest(Message, Options).Content.Simple.Headers
            .ToDictionary(h => h.Name, h => h.Value);

        Assert.Equal("<https://api.example.com/api/email/unsubscribe?t=abc>", headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", headers["List-Unsubscribe-Post"]);
    }

    [Fact]
    public void BuildRequest_TagsCampaignAndOutboxId()
    {
        var tags = SesEmailTransport.BuildRequest(Message, Options).EmailTags.ToDictionary(t => t.Name, t => t.Value);

        Assert.Equal("monthly-recap", tags["campaign"]);
        Assert.Equal("4242", tags["outbox_id"]);
    }

    [Fact]
    public void BuildRequest_SetsBothBodiesAsUtf8()
    {
        var simple = SesEmailTransport.BuildRequest(Message, Options).Content.Simple;

        Assert.Equal("Your November in prints", simple.Subject.Data);
        Assert.Equal("<p>Hi</p>", simple.Body.Html.Data);
        Assert.Equal("Hi", simple.Body.Text.Data);
        Assert.All([simple.Subject.Charset, simple.Body.Html.Charset, simple.Body.Text.Charset], c => Assert.Equal("UTF-8", c));
    }

    // An empty configuration set is "use the account default", which SES rejects as a name.
    [Fact]
    public void BuildRequest_OmitsEmptyConfigurationSet()
        => Assert.Null(SesEmailTransport.BuildRequest(Message, new EmailOptions()).ConfigurationSetName);
}
