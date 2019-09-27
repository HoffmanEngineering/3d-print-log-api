using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.Printer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Profiles
{
    public class PrinterProfile: Profile
    {
        public PrinterProfile()
        {
            CreateMap<Printer, UserPrinterDTO>()
                .ForMember(dest => dest.PrinterId, opt => opt.MapFrom(src => src));

            CreateMap<Printer, PrinterSummary>();
        }
    }
}
