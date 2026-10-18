using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.Printer;

namespace PrintLogApi.Profiles;

public class PrinterProfile : Profile
{
    public PrinterProfile()
    {
        CreateMap<Printer, UserPrinterDTO>()
            .ForMember(dest => dest.PrinterId, opt => opt.MapFrom(src => src));

        CreateMap<Printer, PrinterSummary>();
        CreateMap<Printer, PrinterFeedSummary>();
        CreateMap<Printer, PrinterSummaryWithFilamentDto>();
        CreateMap<Printer, PrinterSummaryWithoutCategory>();
        CreateMap<Printer, PrinterDetailDto>()
            .ForMember(dest => dest.LoadedFilaments, opt => opt.MapFrom(src => src.LoadedFilaments!
                .OrderBy(pf => pf.Slot == null).ThenBy(pf => pf.Slot).ThenBy(pf => pf.LoadedDateTime)));

        // New lightweight mapping for improved query performance
        CreateMap<Printer, PrinterSummarySimpleDto>()
            .ForMember(dest => dest.LoadedFilaments, opt => opt.MapFrom(src => src.LoadedFilaments!
                .OrderBy(pf => pf.Slot == null).ThenBy(pf => pf.Slot).ThenBy(pf => pf.LoadedDateTime)));

        CreateMap<AddPrinterDTO, Printer>()
            .ForMember(dest => dest.Category, opt => opt.Ignore())
            // The id maps by name; the navigation is never loaded from a DTO, and CreatedAt is set once
            // on create and must survive a PUT.
            .ForMember(dest => dest.CatalogModel, opt => opt.Ignore())
            // Omitted (null) keeps the printer's current model, like SlotCount. There is no unlink in v1.
            .ForMember(dest => dest.CatalogModelId, opt => opt.PreCondition(src => src.CatalogModelId != null))
            .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
            // Mapped over a tracked printer, the DTO rows overwrite the loaded rows with copies
            // that have no slot, label or loaded time. PostPrinter builds the rows itself, and
            // PutPrinter diffs the ids through setLoadedFilament.
            .ForMember(dest => dest.LoadedFilaments, opt => opt.Ignore())
            // Omitted by every client that predates slots, so null keeps what the printer has.
            .ForMember(dest => dest.SlotCount, opt =>
            {
                opt.PreCondition(src => src.SlotCount.HasValue);
                opt.MapFrom(src => src.SlotCount!.Value);
            });

        CreateMap<PrinterFilament, PrinterFilamentSummaryDto>()
            .ForMember(dest => dest.Filament, opt => opt.MapFrom(src => src.Filament))
            .ForMember(dest => dest.LoadedAt, opt => opt.MapFrom(src => src.LoadedDateTime))
            .ReverseMap();

        // New lightweight filament mapping for summary views
        CreateMap<PrinterFilament, PrinterFilamentForSummaryDto>()
            .ForMember(dest => dest.Filament, opt => opt.MapFrom(src => src.Filament))
            .ForMember(dest => dest.LoadedAt, opt => opt.MapFrom(src => src.LoadedDateTime));

        CreateMap<AddPrinterFilamentDto, PrinterFilament>();

    }
}
