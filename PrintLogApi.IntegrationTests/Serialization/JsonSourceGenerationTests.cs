using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Serialization;
using Xunit;

namespace PrintLogApi.IntegrationTests.Serialization;

/// <summary>
/// Guards <see cref="PrintLogJsonSerializerContext"/> (#67). Source generation is a pure
/// performance change: it moves serialization metadata from runtime reflection to compile time
/// and must leave the wire contract byte-for-byte identical.
///
/// Three things can go wrong, and each has a test here:
///
/// 1. The reflection fallback gets dropped. Assigning <c>TypeInfoResolver</c> instead of
///    inserting into the chain does exactly that, and the symptom appears on some unrelated
///    endpoint whose payload was never listed in the context — far from the edit that caused it.
/// 2. The context is registered but never consulted, so the whole change is a no-op that still
///    passes every behavioural test in the suite.
/// 3. A type is annotated to fit the generator — <c>required</c> or <c>[JsonRequired]</c> — which
///    AGENTS.md bans because System.Text.Json <em>enforces</em> both, turning a tolerated missing
///    field into a 400.
///
/// Uses the MCP-seeded factory so the endpoints return populated payloads; comparing two
/// serializations of an empty object would prove very little.
/// </summary>
public class JsonSourceGenerationTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
{
    private readonly Mcp.McpDataWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public JsonSourceGenerationTests(Mcp.McpDataWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    /// <summary>The options object <c>Startup</c> configures and the DI container hands out.</summary>
    private JsonSerializerOptions RegisteredOptions =>
        _factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;

    /// <summary>
    /// What responses are ACTUALLY written with, which is not <see cref="RegisteredOptions"/>.
    ///
    /// <c>SystemTextJsonOutputFormatter</c> substitutes
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> when <c>JsonOptions</c> leaves
    /// <c>Encoder</c> null — as this app does — and it does so by <em>copying</em> the options
    /// rather than mutating them. Reproduce that copy or every comparison against a real response
    /// body differs on '+' and '&lt;' for reasons that have nothing to do with #67.
    ///
    /// The copy is also why <see cref="ResolverChain_SurvivesTheOutputFormattersCopy"/> exists:
    /// the resolver chain has to survive it, and nothing in Startup guarantees that.
    /// </summary>
    private JsonSerializerOptions FormatterOptions() =>
        new(RegisteredOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The pre-#67 baseline: the same options the formatter would build, with nothing but the
    /// reflection resolver behind them.
    /// </summary>
    private static JsonSerializerOptions ReflectionOnlyOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

    private static HttpRequestMessage Authed(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    /// <summary>
    /// Every response type the context claims, paired with the endpoint that produces one. Adding
    /// a <c>[JsonSerializable]</c> root without adding a row here leaves it unproven.
    /// </summary>
    public static TheoryData<Type, string> HotResponses() => new()
    {
        { typeof(OverviewResponse), "/api/analytics/overview?timeZone=UTC" },
        { typeof(ActivityResponse), "/api/analytics/activity?timeZone=UTC" },
        { typeof(PrintersResponse), "/api/analytics/printers?timeZone=UTC" },
        { typeof(MaterialsResponse), "/api/analytics/materials?timeZone=UTC" },
        { typeof(CostsResponse), "/api/analytics/costs?timeZone=UTC" },
        { typeof(AccuracyResponse), "/api/analytics/accuracy?timeZone=UTC" },
        { typeof(PagedList<PrintSummaryDTO>), "/api/prints/summary?pageNumber=1&pageSize=25" },
        { typeof(PagedList<PrinterSummarySimpleDto>), "/api/printers/summary?pageNumber=1&pageSize=25" },
    };

    [Fact]
    public void ResolverChain_LeadsWithTheGeneratedContextAndKeepsReflectionBehindIt()
    {
        var chain = RegisteredOptions.TypeInfoResolverChain;

        Assert.Same(PrintLogJsonSerializerContext.Default, chain[0]);

        // The fallback is what lets the context stay partial. Without it, every response type
        // not listed in the context throws at serialization time.
        Assert.Contains(chain, resolver => resolver is DefaultJsonTypeInfoResolver);
    }

    /// <summary>
    /// The formatter serializes through a copy of the registered options, so a chain that is
    /// correct in DI is not yet proof that responses use it. Copy semantics are the framework's
    /// to change, and if they ever stop carrying the chain the change is silent: responses stay
    /// correct and quietly go back to reflection.
    /// </summary>
    [Fact]
    public void ResolverChain_SurvivesTheOutputFormattersCopy()
    {
        var chain = FormatterOptions().TypeInfoResolverChain;

        Assert.Same(PrintLogJsonSerializerContext.Default, chain[0]);
        Assert.Contains(chain, resolver => resolver is DefaultJsonTypeInfoResolver);
    }

    /// <summary>
    /// Registration is not use. A context whose roots do not match the types MVC actually
    /// serializes resolves through the reflection fallback instead, and nothing else in the suite
    /// would notice — the payloads would still be correct, just as slow as before.
    /// </summary>
    [Theory]
    [MemberData(nameof(HotResponses))]
    public void HotResponseTypes_ResolveThroughTheGeneratedContext(Type responseType, string _)
    {
        var typeInfo = FormatterOptions().GetTypeInfo(responseType);

        Assert.Same(PrintLogJsonSerializerContext.Default, typeInfo.OriginatingResolver);
    }

    /// <summary>
    /// The contract proof, and the reason it goes through HTTP rather than a hand-built instance:
    /// the body is what a client receives today.
    ///
    /// The body is read back into the response type using the reflection resolver only, then that
    /// same object is written twice — once through the app's options (source-generated) and once
    /// through reflection-only options. Comparing the two writes to each other proves the
    /// generated contract matches; comparing them to the original body proves the object being
    /// compared is a faithful stand-in for what the controller produced. All three must agree.
    /// </summary>
    [Theory]
    [MemberData(nameof(HotResponses))]
    public async Task GeneratedContract_MatchesTheReflectionContract(Type responseType, string url)
    {
        var response = await _httpClient.SendAsync(Authed(url));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        var reflectionOnly = ReflectionOnlyOptions();
        var payload = JsonSerializer.Deserialize(body, responseType, reflectionOnly);
        Assert.NotNull(payload);

        var viaSourceGeneration = JsonSerializer.Serialize(payload, responseType, FormatterOptions());
        var viaReflection = JsonSerializer.Serialize(payload, responseType, reflectionOnly);

        Assert.Equal(viaReflection, viaSourceGeneration);
        Assert.Equal(body, viaSourceGeneration);
    }

    /// <summary>
    /// Acceptance criterion 2 of #67, kept as a permanent guard rather than a one-time review.
    ///
    /// Both <c>required</c> and <c>[JsonRequired]</c> are enforced by System.Text.Json on
    /// deserialization: a request that omits the member stops binding null and starts returning
    /// 400. Neither belongs on a DTO (see AGENTS.md), and the pressure to add one comes precisely
    /// from annotating types for a serializer context — which is why the check lives here.
    ///
    /// Scoped to Models.DTOs rather than the context's closure: the ban is a DTO rule, and a type
    /// dropping out of the closure should not quietly drop out of the guard.
    /// </summary>
    [Fact]
    public void NoDtoIsRequired()
    {
        var offenders = new List<string>();

        foreach (var type in typeof(Startup).Assembly.GetTypes()
                     .Where(t => t.Namespace?.StartsWith("PrintLogApi.Models.DTOs", StringComparison.Ordinal) == true))
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (member is not (PropertyInfo or FieldInfo))
                {
                    continue;
                }

                if (member.GetCustomAttribute<JsonRequiredAttribute>() is not null)
                {
                    offenders.Add($"{type.FullName}.{member.Name} carries [JsonRequired]");
                }

                if (member.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null)
                {
                    offenders.Add($"{type.FullName}.{member.Name} is declared 'required'");
                }
            }
        }

        Assert.Empty(offenders);
    }
}
