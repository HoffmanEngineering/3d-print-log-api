namespace PrintLogApi.Mcp;

/// <summary>
/// Serves OpenAI's domain-verification token for the ChatGPT plugin directory (#128).
///
/// <para>OpenAI's submission flow proves the submitter controls the MCP server's domain by
/// fetching <c>/.well-known/openai-apps-challenge</c> on the <b>origin</b> of the server URL —
/// <c>https://api.3dprintlog.com</c>, not <c>/mcp</c> beneath it — and expects the token as plain
/// text. The token is issued per organization in the OpenAI Platform dashboard at submission time,
/// so it comes from configuration (<c>Mcp:OpenAiAppsChallengeToken</c>, an App Service setting
/// <c>Mcp__OpenAiAppsChallengeToken</c>) rather than the repo.</para>
///
/// <para>Unset means 404, which is the state before submission and after the listing no longer
/// needs it. The token is not a secret — it is meant to be publicly fetchable — but it is
/// account-specific, which is why it is not committed.</para>
/// </summary>
public static class OpenAiAppsChallenge
{
    public const string Path = "/.well-known/openai-apps-challenge";
    public const string ConfigurationKey = "Mcp:OpenAiAppsChallengeToken";

    public static IEndpointConventionBuilder MapOpenAiAppsChallenge(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, (IConfiguration configuration) =>
        {
            var token = configuration[ConfigurationKey]?.Trim();
            return string.IsNullOrEmpty(token)
                ? Results.NotFound()
                : Results.Text(token, "text/plain");
        }).AllowAnonymous();
}
