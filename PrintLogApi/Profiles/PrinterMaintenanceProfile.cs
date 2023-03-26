
using System;
using System.Linq;
using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.PrinterMaintenance;

namespace PrintLogApi.Profiles
{
    public class PrinterMaintenanceProfile : Profile
    {
        public PrinterMaintenanceProfile() {
            CreateMap<PrinterMaintenance, PrinterMaintenanceDto>().ReverseMap();

            CreateMap<AddPrinterMaintenanceDto, PrinterMaintenance>();

            CreateMap<PutPrinterMaintenanceDto, PrinterMaintenance>();
        
        }
    }
}

