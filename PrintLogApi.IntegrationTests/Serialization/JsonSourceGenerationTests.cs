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
/// Four things can go wrong, and each has a test here:
///
/// 1. The reflection fallback gets dropped. Assigning <c>TypeInfoResolver</c> instead of
///    inserting into the chain does exactly that, and the symptom appears on some unrelated
///    endpoint whose payload was never listed in the context — far from the edit that caused it.
/// 2. The context is registered but never consulted, so the whole change is a no-op that still
///    passes every behavioural test in the suite.
/// 3. The generated metadata describes a different shape than reflection did, for some type in
///    the closure.
/// 4. A type is annotated to fit the generator — <c>required</c> or <c>[JsonRequired]</c> — which
///    AGENTS.md bans because System.Text.Json <em>enforces</em> both, turning a tolerated missing
///    field into a 400.
///
/// On (3), note what a resolver can and cannot change, because it bounds what is worth testing.
/// A resolver supplies <em>metadata</em>: which members are written, under what names, in what
/// order, and how instances are constructed. It does not supply converters — the same
/// <c>DecimalConverter</c>, <c>DateOnlyConverter</c> and friends write the values on both paths, so
/// no amount of fixture variety in the VALUES (offsets, trailing zeros, boundary dates) can make
/// the two paths disagree. What varies per type is the shape, and shape does not depend on the
/// values a fixture happens to carry. So the coverage here is structural and exhaustive —
/// <see cref="GeneratedMetadata_MatchesReflectionMetadata"/> walks all ~100 types in the closure —
/// rather than sampled through hand-built instances that could only ever cover a few of them.
///
/// The endpoint-driven comparison stays alongside it because structural equality is an argument
/// and a real response body is evidence. It uses the MCP-seeded factory so the payloads are
/// non-empty and representative, which is NOT the same as exercising every nested collection,
/// nullable member or cost branch — the structural test is what covers those.
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

    /// <summary>
    /// Every type the generator emitted metadata for, read off the context itself rather than
    /// listed by hand.
    ///
    /// The generator emits one public <c>JsonTypeInfo&lt;T&gt;</c> property per type in the closure
    /// of the declared roots, so this enumerates what was actually generated. That is the property
    /// worth driving a test from: a hand-maintained list would go stale exactly when a root's
    /// graph changes, which is the moment the check matters.
    /// </summary>
    public static IEnumerable<Type> GeneratedClosure() =>
        typeof(PrintLogJsonSerializerContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0]);

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
    /// The exhaustive half of the contract proof: for every type in the generated closure, the
    /// source-generated metadata must describe the same shape reflection did.
    ///
    /// Compares what a resolver is actually responsible for — the kind of the type, and for object
    /// types the ordered list of members with their names, declared types, accessor presence and
    /// required-ness. Name and order together are the wire contract; accessor presence is what
    /// separates a written member from a skipped one; required-ness is the 400-vs-null behaviour
    /// AGENTS.md cares about.
    ///
    /// Reported as one aggregated list rather than a per-type theory: a generator or SDK change
    /// that shifts a convention tends to move many types at once, and seeing all of them beats
    /// fixing them one failing test at a time.
    /// </summary>
    [Fact]
    public void GeneratedMetadata_MatchesReflectionMetadata()
    {
        var sourceGenerated = FormatterOptions();
        var reflected = ReflectionOnlyOptions();
        var mismatches = new List<string>();
        var checkedCount = 0;

        foreach (var type in GeneratedClosure())
        {
            var generatedInfo = sourceGenerated.GetTypeInfo(type);
            var reflectedInfo = reflected.GetTypeInfo(type);

            // Proves the type resolved through the context rather than falling through to the
            // reflection resolver behind it, which would make the comparison below vacuous.
            if (generatedInfo.OriginatingResolver is not PrintLogJsonSerializerContext)
            {
                mismatches.Add($"{type} resolved through {generatedInfo.OriginatingResolver?.GetType().Name ?? "null"}, not the generated context");
                continue;
            }

            if (generatedInfo.Kind != reflectedInfo.Kind)
            {
                mismatches.Add($"{type} kind: generated {generatedInfo.Kind}, reflected {reflectedInfo.Kind}");
            }

            var generatedShape = Shape(generatedInfo);
            var reflectedShape = Shape(reflectedInfo);
            if (generatedShape != reflectedShape)
            {
                mismatches.Add($"{type} members:{Environment.NewLine}  generated: {generatedShape}{Environment.NewLine}  reflected: {reflectedShape}");
            }

            checkedCount++;
        }

        Assert.Empty(mismatches);

        // A context that generated nothing would pass every assertion above vacuously.
        Assert.True(checkedCount > 50, $"Expected the closure of the declared roots to be substantial; walked {checkedCount} types.");
    }

    private static string Shape(JsonTypeInfo typeInfo) =>
        string.Join(
            " | ",
            typeInfo.Properties.Select(p =>
                $"{p.Name}:{p.PropertyType.FullName}" +
                $":get={p.Get is not null}:set={p.Set is not null}:required={p.IsRequired}"));

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
    /// Acceptance criterion 4 of #67: the published contract does not move.
    ///
    /// The full document was diffed byte-for-byte before and after the change and was identical;
    /// this keeps the part of that result which can regress. It is deliberately NOT a snapshot of
    /// the whole document — that churns on every new endpoint and would be deleted within a
    /// release. What it pins is the property naming Swashbuckle emits for a source-generated type,
    /// which is what a serializer-options edit would actually disturb.
    ///
    /// Swashbuckle reads <c>JsonSerializerOptions</c> for its naming policy but does not consult
    /// the resolver chain, so today's answer is "the context is invisible to Swagger". This test is
    /// what would notice if that stopped being true.
    /// </summary>
    [Fact]
    public async Task SwaggerSchema_KeepsCamelCasePropertyNamesForAGeneratedType()
    {
        var response = await _httpClient.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // CustomSchemaIds is type.ToString(), so the schema is keyed by the full CLR name.
        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(typeof(OverviewResponse).ToString());

        var propertyNames = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(
            new[] { "from", "to", "timeZone", "granularity", "tiles", "statusBreakdown", "series", "highlights" },
            propertyNames);
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
