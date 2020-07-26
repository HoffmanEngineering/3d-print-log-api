using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PrintLogApi
{
    public class PrintLogContext : DbContext
    {
        public PrintLogContext(DbContextOptions<PrintLogContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }

        public DbSet<Printer> Printers { get; set; }

        public DbSet<Print> Prints { get; set; }

        public DbSet<File> Files { get; set; }

        public DbSet<PrintImage> PrintImages { get; set; }

        public DbSet<Feedback> Feedback { get; set; }

        public DbSet<UserSetting> UserSettings { get; set; }

        public DbSet<UserSettingType> UserSettingTypes { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserSettingType>().HasData(
                new UserSettingType() { Id = 1, Name = "Prints_DefaultPrintViewStatus", Description = "The Default View Status for a print." },
                new UserSettingType() { Id = 2, Name = "Prints_LastSelectedPrinterId", Description = "The Id of the printer that was last selected." }
                );

            modelBuilder.Entity<User>().HasIndex(u => u.OAuthUserId).IsUnique();
        }

        public override int SaveChanges()
        {
            UpdateTimestamps();

            return base.SaveChanges();
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            UpdateTimestamps();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        private void UpdateTimestamps()
        {
            var entries = ChangeTracker
            .Entries()
            .Where(e => e.Entity is TimestampEntity && (
                    e.State == EntityState.Added
                    || e.State == EntityState.Modified));

            foreach (var entityEntry in entries)
            {
                ((TimestampEntity)entityEntry.Entity).UpdatedDate = DateTime.UtcNow;

                if (entityEntry.State == EntityState.Added)
                {
                    ((TimestampEntity)entityEntry.Entity).CreatedDate = DateTime.UtcNow;
                }
            }
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            UpdateTimestamps();

            return base.SaveChangesAsync(cancellationToken);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            UpdateTimestamps();

            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }
}
