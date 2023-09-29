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

        public DbSet<PrinterCategory> PrinterCategories { get; set; }

        public DbSet<Print> Prints { get; set; }

        public DbSet<PrintComment> PrintComments { get; set; }

        public DbSet<File> Files { get; set; }

        public DbSet<Filament> Filaments { get; set; }

        public DbSet<FilamentAdjustment> FilamentAdjustments { get; set; }

        public DbSet<MaterialCategory> MaterialCategories { get; set; }

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
                new UserSettingType() { Id = 8, Name = "Filaments_DefaultPrice", Description = "The default price of filament, for when pricing wasn't added." },
                new UserSettingType() { Id = 9, Name = "Prints_LastSelectedResinMeasureType", Description = "The last selected resin measure type on the print." },
                new UserSettingType() { Id = 10, Name = "Prints_LastSelectedPowderMeasureType", Description = "The last selected powder measure type on the print." },
                new UserSettingType() { Id = 11, Name = "Prints_LastSelectedWireMeasureType", Description = "The last selected wire measure type on the print." }
                );

            var filamentCategory = new MaterialCategory()
            {
                Nickname = "filament",
                Name = "Filament",
                Description = "A single continuous filament of material",
                HasDiameter = true,
                ShowNozzleTemperature = true,
                ShowBedTemperature = true,
                ShowMeltingTemperature = false,
                ShowInertGas = false,
                ShowMaterialRefreshRatio = false,
                ShowRecommendedInitialLayerTimeInSeconds = false,
                ShowRecommendedLayerTimeInSeconds = false,
                
            };
            var resinCategory = new MaterialCategory()
            {
                Nickname = "resin",
                Name = "Resin",
                Description = "A photo-sensitive resin",
                HasDiameter = false,
                ShowNozzleTemperature = false,
                ShowBedTemperature = false,
                ShowMeltingTemperature = false,
                ShowInertGas = false,
                ShowMaterialRefreshRatio = false,
                ShowRecommendedInitialLayerTimeInSeconds = true,
                ShowRecommendedLayerTimeInSeconds = true,
            };

            var powderCategory = new MaterialCategory()
            {
                Nickname = "powder",
                Name = "Powder",
                Description = "A powder which is fused by heat or a binder",
                HasDiameter = false,
                ShowNozzleTemperature = false,
                ShowBedTemperature = false,
                ShowMeltingTemperature = true,
                ShowInertGas = true,
                ShowMaterialRefreshRatio = true,
                ShowRecommendedInitialLayerTimeInSeconds = false,
                ShowRecommendedLayerTimeInSeconds = false,
            };

            var wireCategory = new MaterialCategory()
            {
                Nickname = "wire",
                Name = "Wire",
                Description = "A continous wire",
                HasDiameter = true,
                ShowNozzleTemperature = true,
                ShowBedTemperature = true,
                ShowMeltingTemperature = false,
                ShowInertGas = false,
                ShowMaterialRefreshRatio = false,
                ShowRecommendedInitialLayerTimeInSeconds = false,
                ShowRecommendedLayerTimeInSeconds = false,
            };

            modelBuilder.Entity<MaterialCategory>().HasData(filamentCategory, resinCategory, powderCategory, wireCategory);

            modelBuilder.Entity<PrinterCategory>().HasData(
                new PrinterCategory()
                {
                    Nickname = "FDM",
                    Name = "Fused Deposition Modeling",
                    Description = "Material extruded through a nozzle.",
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new PrinterCategory()
                {
                    Nickname = "FFF",
                    Name = "Fused Filament Fabrication",
                    Description = "Material extruded through a nozzle.",
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new PrinterCategory()
                {
                    Nickname = "SLA",
                    Name = "Stereolithography",
                    Description = "",
                    MaterialCategoryNickname = resinCategory.Nickname
                },
                new PrinterCategory()
                {
                    Nickname = "LCD",
                    Name = "Liquid Crystal Display",
                    Description = "",
                    MaterialCategoryNickname = resinCategory.Nickname
                },
                new PrinterCategory()
                {
                    Nickname = "DLP",
                    Name = "Digital Light Processing",
                    Description = "",
                    MaterialCategoryNickname = resinCategory.Nickname
                },
                new PrinterCategory()
                {
                    Nickname = "MSLA",
                    Name = "Micro-stereolithography",
                    Description = "",
                    MaterialCategoryNickname = resinCategory.Nickname
                },
                new PrinterCategory()
                {
                    Nickname = "SLS",
                    Name = "Selective Laser Sintering",
                    Description = "",
                    MaterialCategoryNickname = powderCategory.Nickname
                },
                new PrinterCategory()
                {
                    Nickname = "LPDF",
                    Name = "Laser Powder Bed Fusion",
                    Description = "",
                    MaterialCategoryNickname = powderCategory.Nickname
                },
                new PrinterCategory()
                {
                    Nickname = "EBM",
                    Name = "Electron Beam Melting",
                    Description = "",
                    MaterialCategoryNickname = powderCategory.Nickname
                }
            );

            // Pulled from Cura
            modelBuilder.Entity<MaterialType>().HasData(
                new MaterialType()
                {
                    Id = Guid.Parse("c78c56d0-b34d-49b1-849e-a54066a2f5e3"),
                    Acronym = "ABS",
                    Name = "Acrylonitrile Butadiene Styrene",
                    DensityGramPerCubicCm = 1.10,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("c8cae1e0-5f13-41d6-9f72-cb83740aa2fe"),
                    Acronym = "CPE",
                    Name = "Co-polyester",
                    DensityGramPerCubicCm = 1.27,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("b0cda842-5a48-4a30-a060-226680e13c06"),
                    Acronym = "HIPS",
                    Name = "High Impact Polystyrene",
                    DensityGramPerCubicCm = 1.24,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("3dbc49c5-a493-4e21-a4d5-d94b8c0d53da"),
                    Acronym = "Nylon",
                    Name = null,
                    DensityGramPerCubicCm = 1.14,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("7d83cbc1-00d0-4e42-a7ce-8a1b831b175b"),
                    Acronym = "PC",
                    Name = "Polycarbonate",
                    DensityGramPerCubicCm = 1.19,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("35151bfe-6890-41ab-8fc9-443c5a690626"),
                    Acronym = "PCTG",
                    Name = "Cyclohexylenedimethylene Terephthalate Glycol",
                    DensityGramPerCubicCm = 1.24,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("23e38c0d-43f3-4bcd-b3c6-830d193a3e10"),
                    Acronym = "PETG",
                    Name = "Polyethylene Terephthalate Glycol",
                    DensityGramPerCubicCm = 1.38,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("f8a6b364-94a6-4a32-a253-e67b41df1969"),
                    Acronym = "PLA",
                    Name = "Polylactic Acid",
                    DensityGramPerCubicCm = 1.24,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("0c236829-8487-4bb4-a092-68a9731a64e4"),
                    Acronym = "PVA",
                    Name = "Polyvinyl Acetate",
                    DensityGramPerCubicCm = 1.23,
                    MaterialCategoryNickname = filamentCategory.Nickname
                },
                new MaterialType()
                {
                    Id = Guid.Parse("d777bde9-fba6-4f5a-b7a4-e8a4a9695715"),
                    Acronym = "TPU 95A",
                    Name = "Thermoplastic Polyurethane",
                    DensityGramPerCubicCm = 1.22,
                    MaterialCategoryNickname = filamentCategory.Nickname
                }
            );

            modelBuilder.Entity<User>().HasIndex(u => u.OAuthUserId).IsUnique();

            modelBuilder.Entity<CuraSetting>()
                .Property(c => c._Settings)
                .HasColumnName("Settings");

            modelBuilder.Entity<Filament>()
                .Property(f => f.MaterialCategoryNickname)
                .HasDefaultValue("filament");

            // Only every query for currently-loaded filament.
            modelBuilder.Entity<PrinterFilament>()
                .HasQueryFilter(pf => !pf.UnloadedDateTime.HasValue);

            modelBuilder.Entity<PrinterFilament>()
                .HasIndex(pf => pf.PrinterId).IncludeProperties(pf => new { pf.FilamentId, pf.LoadedDateTime, pf.UnloadedDateTime })
                .HasFilter("[UnloadedDateTime] IS NULL");

            modelBuilder.Entity<PrinterFilament>()
                .HasIndex(pf => pf.FilamentId).IncludeProperties(pf => new { pf.PrinterId, pf.LoadedDateTime, pf.UnloadedDateTime })
                .HasFilter("[UnloadedDateTime] IS NULL");

            modelBuilder.Entity<PrinterFilament>()
                .HasIndex(pf => pf.FilamentId).IncludeProperties(pf => new { pf.PrinterId, pf.UnloadedDateTime })
                .HasFilter("[UnloadedDateTime] IS NULL");

            modelBuilder.Entity<PrinterFilament>()
                .HasIndex(pf => pf.PrinterId).IncludeProperties(pf => new { pf.FilamentId, pf.UnloadedDateTime })
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
