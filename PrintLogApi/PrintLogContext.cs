using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi
{
    public class PrintLogContext : DbContext
    {
        public PrintLogContext(DbContextOptions<PrintLogContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }

        public DbSet<Comment> Comments { get; set; }

        public DbSet<Printer> Printers { get; set; }

        public DbSet<Print> Prints { get; set; }

        public DbSet<PrintComment> PrintComments { get; set; }

        public DbSet<File> Files { get; set; }

        public DbSet<Filament> Filaments { get; set; }

        public DbSet<PrintFilament> PrintFilament { get; set; }

        public DbSet<PrintImage> PrintImages { get; set; }

        public DbSet<Feedback> Feedback { get; set; }

        public DbSet<UserSetting> UserSettings { get; set; }

        public DbSet<UserSettingType> UserSettingTypes { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserSettingType>().HasData(
                new UserSettingType() { Id = 1, Name = "Prints_DefaultPrintViewStatus", Description = "The Default View Status for a print." },
                new UserSettingType() { Id = 2, Name = "Prints_LastSelectedPrinterId", Description = "The Id of the printer that was last selected." },
                new UserSettingType() { Id = 3, Name = "Prints_LastSelectedAllowComments", Description = "The value of the last changed Allow Comments on prints." }
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
                var saveDateTime = DateTime.UtcNow;
                ((TimestampEntity)entityEntry.Entity).UpdatedDate = saveDateTime;

                if (entityEntry.State == EntityState.Added)
                {
                    ((TimestampEntity)entityEntry.Entity).CreatedDate = saveDateTime;
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
