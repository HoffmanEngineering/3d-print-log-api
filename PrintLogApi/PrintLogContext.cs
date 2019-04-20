using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi
{
    public class PrintLogContext : DbContext
    {
        public PrintLogContext(DbContextOptions options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
    }
}
