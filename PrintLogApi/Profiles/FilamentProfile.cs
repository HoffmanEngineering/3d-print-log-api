using System.Linq.Expressions;
using AutoMapper;
using PrintLogApi.Enums;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Profiles;

public class FilamentProfile : Profile
{
    /// <summary>
    /// Milligrams one usage row took off its spool: the actual amount where one was recorded,
    /// the estimate otherwise, in whichever measure the row carries. PrintService normalizes a
    /// saved row into all three measures, so AmountMg is usually present, but a row written
    /// around that path (seeded data, an import, a filament with no usable diameter at save time)
    /// carries only what the user entered, and milligrams are the common denominator.
    ///
    /// <para>Density and diameter are read through the row's own <c>Filament</c> navigation,
    /// never off the outer spool: SQL Server refuses a correlated aggregate that reads several
    /// outer columns (error 8124, see FilamentSqlServerTranslationTests).</para>
    ///
    /// <para>This is the one copy of the rule. Every spool figure below is built from it, so
    /// the web app, the MCP tools, analytics and the Spoolman API cannot disagree.</para>
    /// </summary>
    public static readonly Expression<Func<PrintFilament, double>> UsageRowMg = p =>
        p.AmountMg.HasValue && p.AmountMg > 0 ? (double)p.AmountMg.Value
        : p.VolumeMl.HasValue && p.VolumeMl > 0 ? p.VolumeMl.Value * p.Filament!.MaterialDensityGramPerCubicCm * 1000.0
        : p.LengthInM.HasValue && p.LengthInM > 0 && p.Filament!.DiameterMm.HasValue && p.Filament.DiameterMm > 0
            ? 250.0 * Math.PI * p.Filament!.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.LengthInM.Value
        : p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ? (double)p.EstimatedAmountMg.Value
        : p.EstimatedVolumeMl.HasValue && p.EstimatedVolumeMl > 0 ? p.EstimatedVolumeMl.Value * p.Filament!.MaterialDensityGramPerCubicCm * 1000.0
        : p.EstimatedLengthInM.HasValue && p.EstimatedLengthInM > 0 && p.Filament!.DiameterMm.HasValue && p.Filament.DiameterMm > 0
            ? 250.0 * Math.PI * p.Filament!.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.EstimatedLengthInM.Value
        : 0.0;

    /// <summary>Milligrams the spool's prints took off it, by <see cref="UsageRowMg"/>.</summary>
    private static readonly Expression<Func<Filament, double>> PrintUsageMg =
        ExpressionSplice.Inline<Filament, Func<PrintFilament, double>, double>((f, row) => f.PrintFilaments!.Sum(row), UsageRowMg);

    /// <summary>
    /// Milligrams taken off the spool by prints and by adjustments together: print usage less the
    /// net adjustment. An adjustment is signed (negative removes filament, positive adds it back)
    /// and is normalized into AmountMg when saved, whatever measure it was entered in.
    /// </summary>
    private static readonly Expression<Func<Filament, double>> ConsumedMgExact = ExpressionSplice.With<Filament, double, double>(
        (f, prints) => prints - f.FilamentAdjustments!.Sum(adj => (double)(adj.AmountMg ?? 0)),
        PrintUsageMg);

    /// <summary>
    /// Remaining milligrams as the detail page reports it, or null for a spool with no nominal
    /// weight (untracked): the nominal weight less <see cref="ConsumedMg"/>. May be negative when
    /// more usage was logged than the spool held; callers decide whether to clamp.
    /// </summary>
    public static readonly Expression<Func<Filament, long?>> RemainingMg = ExpressionSplice.With<Filament, double, long?>(
        (f, consumed) => f.InitialNominalWeightMg.HasValue ? (long?)Math.Round(f.InitialNominalWeightMg.Value - consumed) : null,
        ConsumedMgExact);

    /// <summary>
    /// <see cref="RemainingMg"/> as the list, the MCP inventory and loaded-filament views report
    /// it: an untracked spool counts from zero instead of reporting null, so it reads as minus
    /// whatever it has consumed. Kept as it was; whether those views should say "not tracked"
    /// like the detail page is a separate, caller-visible decision.
    /// </summary>
    public static readonly Expression<Func<Filament, long?>> BalanceMg = ExpressionSplice.With<Filament, double, long?>(
        (f, consumed) => (long?)Math.Round((f.InitialNominalWeightMg ?? 0) - consumed),
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
            // Prints and adjustments both count; see UsageRowMg and BalanceMg above.
            .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(BalanceMg))
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
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

        CreateMap<EditFilamentDto, Filament>()
            .ForMember(dest => dest.MaterialCategoryNickname, src => src.MapFrom(src => !String.IsNullOrEmpty(src.MaterialCategoryNickname) ? src.MaterialCategoryNickname : "filament"))
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

        CreateMap<FilamentDetailDto, Filament>()
            .ForMember(dest => dest.SpoolNumber, opt => opt.Ignore())
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

        CreateMap<Filament, FilamentDetailDto>()
            // Ordered so a client diffing the list sees a stable order. GetFilamentById Includes Tags.
            .ForMember(dest => dest.TagUids, src => src.MapFrom(src => src.Tags.Select(t => t.Uid).OrderBy(u => u).ToList()))
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
