using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.PrinterCategory;

namespace PrintLogApi.Profiles
{
    public class PrinterCategoryProfile: Profile
    {
        public PrinterCategoryProfile()
        {
            CreateMap<PrinterCategory, PrinterCategoryDto>()
                .ReverseMap();
        }
    }
}
