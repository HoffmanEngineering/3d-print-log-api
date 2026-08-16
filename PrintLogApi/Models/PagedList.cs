using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class PagedList<T>
    {
        public PageInfo Paging { get; set; }

        public List<T> Items { get; set; }

        /// <summary>
        /// For Integration Testing Deserialization Only
        /// </summary>
        public PagedList()
        {
            Paging = new PageInfo(1, 1, 1);
            Items = new List<T>();
        }

        public PagedList(List<T> items, int totalCount, int pageNumber, int pageSize)
        {
            Paging = new PageInfo(totalCount, pageNumber, pageSize);
            Items = items;
        }

        public static PagedList<T> Create(IQueryable<T> source, int pageNumber, int pageSize)
        {
            var count = source.Count();
            var items = source.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
            return new PagedList<T>(items, count, pageNumber, pageSize);
        }

        public static async Task<PagedList<T>> CreateAsync(IQueryable<T> source, int pageNumber, int pageSize)
        {
            var count = await source.CountAsync();
            var items = await source.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();
            return new PagedList<T>(items, count, pageNumber, pageSize);
        }
    }
}
