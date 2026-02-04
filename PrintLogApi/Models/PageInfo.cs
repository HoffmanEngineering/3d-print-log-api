using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class PageInfo
    {
        public int CurrentPage { get; set; }

        public int TotalPages { get; set; }

        public int PageSize { get; set; }

        public int TotalCount { get; set; }

        public PageInfo(int totalCount, int pageNumber, int pageSize)
        {
            TotalCount = totalCount;
            PageSize = pageSize;
            CurrentPage = pageNumber;
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        }

        /// <summary>
        /// For Integration Testing Deserialization Only
        /// </summary>
        public PageInfo()
        {
            TotalCount = 1;
            PageSize = 1;
            CurrentPage = 1;
            TotalPages = 1;
        }
    }
}
