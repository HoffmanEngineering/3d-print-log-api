using System.Text.Json.Serialization;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;

namespace PrintLogApi.Serialization;

/// <summary>
/// Compile-time serialization metadata for the highest-volume response payloads (#67).
///
/// This is a <em>partial</em> resolver, deliberately. It is inserted at the head of the MVC
/// options' <c>TypeInfoResolverChain</c> with <see cref="System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver"/>
/// still behind it, so a type that is not listed here resolves by reflection exactly as before
/// rather than throwing. Registration lives in <c>Startup.ConfigureServices</c>; the chain order
/// is asserted by <c>JsonSourceGenerationTests</c>, because a chain with the fallback dropped
/// fails at request time on some other endpoint, not here.
///
/// Only the roots are listed. The generator walks each root's type graph and emits metadata for
/// every reachable type, so <c>Coverage</c>, <c>Metric</c>, <c>PrinterCategoryDto</c> and the rest
/// are covered without being named.
///
/// <para><b>Do not add <c>required</c> or <c>[JsonRequired]</c> to a type to make it fit here.</b>
/// See the DTO section of AGENTS.md: <c>System.Text.Json</c> enforces both, so either one turns a
/// tolerated missing field into a 400. Source generation changes where the metadata comes from and
/// nothing about the contract.</para>
/// </summary>
/// <remarks>
/// <para><see cref="JsonSourceGenerationOptionsAttribute"/> carries
/// <see cref="System.Text.Json.JsonSerializerDefaults.Web"/> to match what ASP.NET Core constructs
/// for <c>JsonOptions.JsonSerializerOptions</c>. It does not affect responses — a resolver in a
/// chain supplies metadata and the runtime options win on naming and casing — but it keeps
/// <c>Default.Options</c> honest for anyone who serializes through the context directly, which
/// without it would silently be PascalCase.</para>
///
/// <para>What this buys is the metadata path, not System.Text.Json's generated fast-path writer.
/// The fast path requires <c>JsonSerializerOptions.Encoder</c> to be null, and MVC's
/// <c>SystemTextJsonOutputFormatter</c> writes through a copy of the options with
/// <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> substituted in — so it is out of reach for
/// any MVC response, whatever this context declares. The saving is compile-time property discovery
/// and build-time member accessors in place of runtime reflection and IL emit.</para>
/// </remarks>
[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
// The two cached list endpoints. Cached responses are serialized on every cache hit, which makes
// them the highest-volume writes in the app.
[JsonSerializable(typeof(PagedList<PrintSummaryDTO>))]
[JsonSerializable(typeof(PagedList<PrinterSummarySimpleDto>))]
// The six analytics tabs. Each is a deep record graph, which is where reflection costs the most.
[JsonSerializable(typeof(OverviewResponse))]
[JsonSerializable(typeof(ActivityResponse))]
[JsonSerializable(typeof(PrintersResponse))]
[JsonSerializable(typeof(MaterialsResponse))]
[JsonSerializable(typeof(CostsResponse))]
[JsonSerializable(typeof(AccuracyResponse))]
public partial class PrintLogJsonSerializerContext : JsonSerializerContext;
