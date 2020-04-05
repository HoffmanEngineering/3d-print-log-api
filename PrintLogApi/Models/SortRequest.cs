using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class SortRequest<T> where T : System.Enum
    {
        public SortDirection SortDirection { get; set; }

        public T SortColumn { get; set; }
    }
}
