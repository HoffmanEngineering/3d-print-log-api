using System.Linq.Expressions;
using AutoMapper;
using PrintLogApi.Enums;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.SpoolTags;

namespace PrintLogApi.Profiles;

public class FilamentProfile : Profile
{
    /// <summary>
    /// The actual side of the usage-row rule. A row took <see cref="UsageRowMg"/> milligrams off
    /// its spool: the actual amount where one was recorded (this expression), the estimate
    /// otherwise, in whichever measure the row carries. PrintService normalizes a
    /// saved row into all three measures, so AmountMg is usually present, but a row written
    /// around that path (seeded data, an import, a filament with no usable diameter at save time)
    /// carries only what the user entered, and milligrams are the common denominator.
    ///
    /// <para>Density and diameter are read through the row's own <c>Filament</c> navigation,
    /// never off the outer spool: SQL Server refuses a correlated aggregate that reads several
    /// outer columns (error 8124, see FilamentSqlServerTranslationTests).</para>
    ///
    /// <para>Volume and length need the spool's density (and diameter), so a row with no spool
    /// converts only a weight; its other measures count as not recorded and fall through. The
    /// <c>p.Filament != null</c> guards say so explicitly, which also keeps the rule safe to
    /// evaluate in memory over a row whose spool is gone.</para>
    ///
    /// <para>This is the one copy of the rule. Every spool figure below, and every per-print
    /// total (#229, see <see cref="PrintMetrics"/>), is built from it, so the web app, the MCP
    /// tools, analytics and the Spoolman API cannot disagree.</para>
    /// </summary>
    public static readonly Expression<Func<PrintFilament, double>> UsageRowActualMg = p =>
        p.AmountMg.HasValue && p.AmountMg > 0 ? (double)p.AmountMg.Value
        : p.VolumeMl.HasValue && p.VolumeMl > 0 && p.Filament != null
            ? p.VolumeMl.Value * p.Filament.MaterialDensityGramPerCubicCm * 1000.0
        : p.LengthInM.HasValue && p.LengthInM > 0 && p.Filament != null && p.Filament.DiameterMm.HasValue && p.Filament.DiameterMm > 0
            ? 250.0 * Math.PI * p.Filament.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.LengthInM.Value
        : 0.0;

    /// <summary>The estimated side of <see cref="UsageRowMg"/>: the same conversions, on the estimated measures.</summary>
    public static readonly Expression<Func<PrintFilament, double>> UsageRowEstimatedMg = p =>
        p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ? (double)p.EstimatedAmountMg.Value
        : p.EstimatedVolumeMl.HasValue && p.EstimatedVolumeMl > 0 && p.Filament != null
            ? p.EstimatedVolumeMl.Value * p.Filament.MaterialDensityGramPerCubicCm * 1000.0
        : p.EstimatedLengthInM.HasValue && p.EstimatedLengthInM > 0 && p.Filament != null && p.Filament.DiameterMm.HasValue && p.Filament.DiameterMm > 0
            ? 250.0 * Math.PI * p.Filament.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.EstimatedLengthInM.Value
        : 0.0;

    /// <summary>The resolved figure: <see cref="UsageRowActualMg"/> where the row has one, <see cref="UsageRowEstimatedMg"/> otherwise.</summary>
    public static readonly Expression<Func<PrintFilament, double>> UsageRowMg = ExpressionSplice.With<PrintFilament, double, double, double>(
        (p, actual, estimated) => actual > 0 ? actual : estimated,
        UsageRowActualMg,
        UsageRowEstimatedMg);

    /// <summary>True when <see cref="UsageRowMg"/> fell back to the estimate: no usable actual, and an estimate there is.</summary>
    public static readonly Expression<Func<PrintFilament, bool>> UsageRowIsEstimated = ExpressionSplice.With<PrintFilament, double, double, bool>(
        (p, actual, estimated) => !(actual > 0) && estimated > 0,
        UsageRowActualMg,
        UsageRowEstimatedMg);

    /// <summary>Milligrams the spool's prints took off it, by <see cref="UsageRowMg"/>.</summary>
    private static readonly Expression<Func<Filament, double>> PrintUsageMg =
        ExpressionSplice.Inline<Filament, Func<PrintFilament, double>, double>((f, row) => f.PrintFilaments!.Sum(row), UsageRowMg);

    /// <summary>
    /// One adjustment's signed milligrams, as the spool sees it: negative removes filament, positive
    /// adds it back. An adjustment is normalized into AmountMg when saved, whatever measure it was
    /// entered in, so a row without one counts as nothing. The consumption an adjustment represents
    /// is the negation of this (Materials analytics, #231).
    /// </summary>
    public static readonly Expression<Func<FilamentAdjustment, double>> AdjustmentAmountMg =
        adj => (double)(adj.AmountMg ?? 0);

    /// <summary>The spool's net adjustment, by <see cref="AdjustmentAmountMg"/>.</summary>
    private static readonly Expression<Func<Filament, double>> NetAdjustmentMg =
        ExpressionSplice.Inline<Filament, Func<FilamentAdjustment, double>, double>((f, adjustment) => f.FilamentAdjustments!.Sum(adjustment), AdjustmentAmountMg);

    /// <summary>
    /// Milligrams taken off the spool by prints and by adjustments together: print usage less the
    /// net adjustment (<see cref="AdjustmentAmountMg"/>).
    /// </summary>
    private static readonly Expression<Func<Filament, double>> ConsumedMgExact = ExpressionSplice.With<Filament, double, double, double>(
        (f, prints, adjustments) => prints - adjustments,
        PrintUsageMg,
        NetAdjustmentMg);

    /// <summary>
    /// Remaining milligrams: the nominal weight less <see cref="ConsumedMg"/>, or null for a spool
    /// with no nominal weight (untracked). May be negative when more usage was logged than the
    /// spool held; callers decide whether to clamp.
    ///
    /// <para>This is the only remaining figure (#230). The detail page, the list, the printer
    /// page, the MCP tools, analytics and the Spoolman API all report it, so an untracked spool
    /// is null ("not tracked") everywhere rather than counting down from zero on some surfaces.
    /// A caller that wants how much has left the spool regardless of tracking wants
    /// <see cref="ConsumedMg"/>, not a remaining figure that pretends the spool started empty.</para>
    /// </summary>
    public static readonly Expression<Func<Filament, long?>> RemainingMg = ExpressionSplice.With<Filament, double, long?>(
        (f, consumed) => f.InitialNominalWeightMg.HasValue ? (long?)Math.Round(f.InitialNominalWeightMg.Value - consumed) : null,
        ConsumedMgExact);

    /// <summary>
    /// Milligrams taken off the spool by prints and adjustments, rounded. For a tracked spool this
    /// is exactly nominal weight less <see cref="RemainingMg"/>, which is the invariant Spoolman
    /// clients rely on (<c>used_weight + remaining_weight = initial_weight</c>).
    /// </summary>
    public static readonly Expression<Func<Filament, long>> ConsumedMg = ExpressionSplice.With<Filament, double, long>(
        (f, consumed) => (long)Math.Round(consumed),
        ConsumedMgExact);

    /// <summary>
    /// Milligrams consumed by prints alone, actual where recorded and estimated otherwise.
    /// Adjustments are deliberately left out: this is the detail page's "used in prints" figure.
    /// For how much has left the spool, use <see cref="ConsumedMg"/>.
    /// </summary>
    public static readonly Expression<Func<Filament, long>> UsedMg = ExpressionSplice.With<Filament, double, long>(
        (f, prints) => (long)Math.Round(prints),
        PrintUsageMg);

    /// <summary>
    /// A tag as the detail page reports it. Stored JSON that no longer reads (a hand edit, an old
    /// shape) gives a null reading rather than failing the whole response.
    /// </summary>
    private static FilamentTagDetailDto ToTagDetail(FilamentTag tag)
    {
        TagReading? reading = null;
        try
        {
            reading = TagReadingJson.Deserialize(tag.DecodedJson);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return new FilamentTagDetailDto
        {
            Uid = tag.Uid,
            Format = tag.Format,
            Reading = reading,
            LastReadAt = tag.LastReadAt,
        };
    }

    public FilamentProfile()
    {
        // Lightweight mapping for printer summary views - avoids expensive calculations
        CreateMap<Filament, FilamentSummaryForPrinterDto>()
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()));

        CreateMap<Filament, FilamentSummaryDto>()
            .ForMember(dest => dest.LoadedInPrinter, src => src.MapFrom(src => src.PrinterFilaments!.Where(pf => !pf.UnloadedDateTime.HasValue).Select(p => p.Printer).FirstOrDefault()))
            // Volume and length are NOT mapped here: FilamentSummaryDto derives them from
            // this figure so the list, the detail page and each other cannot disagree.
            // Prints and adjustments both count, and an untracked spool is null as on the
            // detail page; see UsageRowMg and RemainingMg above.
            .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(RemainingMg))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()))
            .ForMember(dest => dest.ColorHex, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors[0] : src.ColorHex));

        // SpoolNumber is ignored on every inbound map: it is assigned on create and never
        // settable by a client (#174), so a PUT echoing back a different number is a no-op.
        CreateMap<FilamentSummaryDto, Filament>()
            .ForMember(dest => dest.SpoolNumber, opt => opt.Ignore());

        CreateMap<AddFilamentDto, Filament>()
            .ForMember(dest => dest.MaterialCategoryNickname, src => src.MapFrom(src => !String.IsNullOrEmpty(src.MaterialCategoryNickname) ? src.MaterialCategoryNickname : "filament"))
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects))
            // Normalized and stored by the service.
            .ForMember(dest => dest.Barcodes, opt => opt.Ignore());

        CreateMap<EditFilamentDto, Filament>()
            .ForMember(dest => dest.MaterialCategoryNickname, src => src.MapFrom(src => !String.IsNullOrEmpty(src.MaterialCategoryNickname) ? src.MaterialCategoryNickname : "filament"))
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

        CreateMap<FilamentDetailDto, Filament>()
            .ForMember(dest => dest.SpoolNumber, opt => opt.Ignore())
            // Only a create sets the catalog entry (validated); a PUT can never change it.
            .ForMember(dest => dest.CatalogEntryId, opt => opt.Ignore())
            // Barcodes change only through their own endpoints.
            .ForMember(dest => dest.Barcodes, opt => opt.Ignore())
            // Response-only and request-only tag members: the service applies readings itself.
            .ForSourceMember(src => src.TagDetails, opt => opt.DoNotValidate())
            .ForSourceMember(src => src.TagReadings, opt => opt.DoNotValidate())
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects))
            // A missing/null list keeps existing adjustment history; an explicit empty list clears it.
            .ForMember(dest => dest.FilamentAdjustments, opt => opt.PreCondition(src => src.FilamentAdjustments is not null));

        CreateMap<Filament, FilamentDetailDto>()
            // Ordered so a client diffing the list sees a stable order. GetFilamentById Includes Tags.
            .ForMember(dest => dest.TagUids, src => src.MapFrom(src => src.Tags.Select(t => t.Uid).OrderBy(u => u).ToList()))
            .ForMember(dest => dest.TagDetails, src => src.MapFrom(src => src.Tags.OrderBy(t => t.Uid).Select(ToTagDetail).ToList()))
            // GetFilamentById Includes Barcodes; elsewhere the navigation is null and so is the list.
            .ForMember(dest => dest.Barcodes, src => src.MapFrom(src => src.Barcodes == null ? null : src.Barcodes
                .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
                .Select(b => new FilamentBarcodeDto { Id = b.Id, Value = b.Value, Format = b.Format }).ToList()))
            // Volume and length are NOT mapped here: FilamentDetailDto derives them from
            // this figure, which also gives them the untracked-spool null for free. Without
            // that an untracked spool would return remaining null alongside length 0 and
            // volume 0, and the UI would render "Not tracked" beside a contradictory "0.0 m".
            .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(RemainingMg))
            // Distinct prints, not usage rows: there is no unique index on (PrintId, FilamentId),
            // so one print may hold two rows for the same spool.
            //
            // Read PrintId, never p.Print. GetFilamentById Includes PrintFilaments but not the
            // Print behind each one, and there is no lazy-loading proxy, so reaching through that
            // navigation here would NullReferenceException on every filament that has usage.
            .ForMember(dest => dest.PrintCount, src => src.MapFrom(src => src.PrintFilaments!.Select(p => p.PrintId).Distinct().Count()))
            .ForMember(dest => dest.TotalUsedMg, src => src.MapFrom(UsedMg))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()))
            .ForMember(dest => dest.ColorHex, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors[0] : src.ColorHex));

        CreateMap<FilamentAdjustment, FilamentAdjustmentDto>();

        CreateMap<FilamentAdjustmentDto, FilamentAdjustment>()
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : FilamentAdjustment.SourceMeasurement.Weight));
    }
}
