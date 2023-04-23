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

        public DbSet<CuraSetting> CuraSettings { get; set; }

        public DbSet<Printer> Printers { get; set; }

        public DbSet<Print> Prints { get; set; }

        public DbSet<PrintComment> PrintComments { get; set; }

        public DbSet<File> Files { get; set; }

        public DbSet<Filament> Filaments { get; set; }

        public DbSet<FilamentAdjustment> FilamentAdjustments { get; set; }

        public DbSet<MaterialType> MaterialTypes { get; set; }

        public DbSet<PrintFilament> PrintFilament { get; set; }

        public DbSet<PrinterFilament> PrinterFilament { get; set; }

        public DbSet<PrinterMaintenance> PrinterMaintenance { get; set; }

        public DbSet<PrintImage> PrintImages { get; set; }

        public DbSet<Feedback> Feedback { get; set; }

        public DbSet<UserApiKey> UserApiKeys { get; set; }

        public DbSet<UserSetting> UserSettings { get; set; }

        public DbSet<UserSettingType> UserSettingTypes { get; set; }

        public static int fnNaturalSort(string sortKey)
            => throw new NotSupportedException();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserSettingType>().HasData(
                new UserSettingType() { Id = 1, Name = "Prints_DefaultPrintViewStatus", Description = "The Default View Status for a print." },
                new UserSettingType() { Id = 2, Name = "Prints_LastSelectedPrinterId", Description = "The Id of the printer that was last selected." },
                new UserSettingType() { Id = 3, Name = "Prints_LastSelectedAllowComments", Description = "The value of the last changed Allow Comments on prints." },
                new UserSettingType() { Id = 4, Name = "Prints_LastSelectedFilamentMeasureType", Description = "The last selected filament measure type on the print." },
                new UserSettingType() { Id = 5, Name = "Currency_Name", Description = "The three-character country code of the preferred currency" },
                new UserSettingType() { Id = 6, Name = "Currency_Symbol", Description = "The symbol of the preferred currency" },
                new UserSettingType() { Id = 7, Name = "Filaments_DefaultDiameterMm", Description = "The default diameter of new filament (in millimeters)." },
                new UserSettingType() { Id = 8, Name = "Filaments_DefaultPrice", Description = "The default price of filament, for when pricing wasn't added." }
                );

            // Pulled from Cura
            modelBuilder.Entity<MaterialType>().HasData(
                new MaterialType()
                {
                    Id = Guid.Parse("c78c56d0-b34d-49b1-849e-a54066a2f5e3"),
                    Acronym = "ABS",
                    Name = "Acrylonitrile Butadiene Styrene",
                    DensityGramPerCubicCm = 1.10
                },
                new MaterialType()
                {
                    Id = Guid.Parse("c8cae1e0-5f13-41d6-9f72-cb83740aa2fe"),
                    Acronym = "CPE",
                    Name = "Co-polyester",
                    DensityGramPerCubicCm = 1.27
                },
                new MaterialType()
                {
                    Id = Guid.Parse("b0cda842-5a48-4a30-a060-226680e13c06"),
                    Acronym = "HIPS",
                    Name = "High Impact Polystyrene",
                    DensityGramPerCubicCm = 1.24
                },
                new MaterialType()
                {
                    Id = Guid.Parse("3dbc49c5-a493-4e21-a4d5-d94b8c0d53da"),
                    Acronym = "Nylon",
                    Name = null,
                    DensityGramPerCubicCm = 1.14
                },
                new MaterialType()
                {
                    Id = Guid.Parse("7d83cbc1-00d0-4e42-a7ce-8a1b831b175b"),
                    Acronym = "PC",
                    Name = "Polycarbonate",
                    DensityGramPerCubicCm = 1.19
                },
                new MaterialType()
                {
                    Id = Guid.Parse("35151bfe-6890-41ab-8fc9-443c5a690626"),
                    Acronym = "PCTG",
                    Name = "Cyclohexylenedimethylene Terephthalate Glycol",
                    DensityGramPerCubicCm = 1.24
                },
                new MaterialType()
                {
                    Id = Guid.Parse("23e38c0d-43f3-4bcd-b3c6-830d193a3e10"),
                    Acronym = "PETG",
                    Name = "Polyethylene Terephthalate Glycol",
                    DensityGramPerCubicCm = 1.38
                },
                new MaterialType()
                {
                    Id = Guid.Parse("f8a6b364-94a6-4a32-a253-e67b41df1969"),
                    Acronym = "PLA",
                    Name = "Polylactic Acid",
                    DensityGramPerCubicCm = 1.24
                },
                new MaterialType()
                {
                    Id = Guid.Parse("0c236829-8487-4bb4-a092-68a9731a64e4"),
                    Acronym = "PVA",
                    Name = "Polyvinyl Acetate",
                    DensityGramPerCubicCm = 1.23
                },
                new MaterialType()
                {
                    Id = Guid.Parse("d777bde9-fba6-4f5a-b7a4-e8a4a9695715"),
                    Acronym = "TPU 95A",
                    Name = "Thermoplastic Polyurethane",
                    DensityGramPerCubicCm = 1.22
                }
            );

            modelBuilder.Entity<User>().HasIndex(u => u.OAuthUserId).IsUnique();

            modelBuilder.Entity<CuraSetting>()
                .Property(c => c._Settings)
                .HasColumnName("Settings");

            // Only every query for currently-loaded filament.
            modelBuilder.Entity<PrinterFilament>()
                .HasQueryFilter(pf => !pf.UnloadedDateTime.HasValue);

            modelBuilder.Entity<PrinterFilament>()
                .HasIndex(pf => pf.PrinterId).IncludeProperties(pf => new { pf.FilamentId, pf.LoadedDateTime, pf.UnloadedDateTime })
                .HasFilter("[UnloadedDateTime] IS NULL");

            modelBuilder.Entity<PrinterFilament>()
                .HasIndex(pf => pf.FilamentId).IncludeProperties(pf => new { pf.PrinterId, pf.LoadedDateTime, pf.UnloadedDateTime })
                .HasFilter("[UnloadedDateTime] IS NULL");

            modelBuilder.Entity<PrintFilament>().HasIndex(pf => pf.PrintId).IncludeProperties(pf => new { pf.FilamentId, pf.AmountMg, pf.EstimatedAmountMg });

            modelBuilder.Entity<PrinterMaintenance>().HasIndex(pm => pm.CreatedById).IncludeProperties(pm => new { pm.Date, pm.CreatedDate});

            modelBuilder.HasDbFunction(typeof(PrintLogContext).GetMethod(nameof(fnNaturalSort), new[] { typeof(string) }))
                .HasName("fnNaturalSort");
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
