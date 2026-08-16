using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

        // Qualified because ImplicitUsings brings System.IO into scope, which makes a bare "File"
        // ambiguous with System.IO.File. Matches how PrintImageService already spells it.
        public DbSet<Models.File> Files { get; set; }

        public DbSet<Filament> Filaments { get; set; }

        public DbSet<FilamentAdjustment> FilamentAdjustments { get; set; }

        public DbSet<MaterialCategory> MaterialCategories { get; set; }

        public DbSet<MaterialType> MaterialTypes { get; set; }

        public DbSet<PrintFilament> PrintFilament { get; set; }

        public DbSet<PrinterFilament> PrinterFilament { get; set; }

        public DbSet<PrinterMaintenance> PrinterMaintenance { get; set; }

        public DbSet<PrintImage> PrintImages { get; set; }

        public DbSet<PrintAttachment> PrintAttachments { get; set; }

        public DbSet<Project> Projects { get; set; }
        public DbSet<ProjectImage> ProjectImages { get; set; }

        public DbSet<Feedback> Feedback { get; set; }

        public DbSet<UserApiKey> UserApiKeys { get; set; }

        public DbSet<UserSetting> UserSettings { get; set; }

        public DbSet<UserSettingType> UserSettingTypes { get; set; }

        public DbSet<Notification> Notifications { get; set; }

        public DbSet<Subscription> Subscriptions { get; set; }

        public DbSet<McpIdempotencyRecord> McpIdempotencyRecords { get; set; }

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
                new UserSettingType() { Id = 11, Name = "Prints_LastSelectedWireMeasureType", Description = "The last selected wire measure type on the print." },
                new UserSettingType() { Id = 14, Name = "Prints_PreferredFilamentDisplayUnit", Description = "The user's preferred unit for displaying filament usage (1=Weight, 2=Length, 3=Volume)." }
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

            var FFFPrinterCategory = new PrinterCategory()
            {
                Nickname = "FFF",
                Name = "Fused Filament Fabrication",
                Description = "Material extruded through a nozzle.",
                MaterialCategoryNickname = filamentCategory.Nickname,
                ShowFilamentDiameter = true,
                ShowNozzleDiameter = true,
                ShowBeamDiameter = false,
                ShowBedSize = true,
                ShowHasHeatedBed = true,
                ShowHasHeatedChamber = true,
                ShowScreenResolution = false,
            };

            modelBuilder.Entity<PrinterCategory>().HasData(
                new PrinterCategory()
                {
                    Nickname = "FDM",
                    Name = "Fused Deposition Modeling",
                    Description = "Material extruded through a nozzle.",
                    MaterialCategoryNickname = filamentCategory.Nickname,
                    ShowFilamentDiameter = true,
                    ShowNozzleDiameter = true,
                    ShowBeamDiameter = false,
                    ShowBedSize = true,
                    ShowHasHeatedBed = true,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = false,
                },
                FFFPrinterCategory,
                new PrinterCategory()
                {
                    Nickname = "SLA",
                    Name = "Stereolithography",
                    Description = "Laser based SLA which cures photosensitive resin.",
                    MaterialCategoryNickname = resinCategory.Nickname,
                    ShowFilamentDiameter = false,
                    ShowNozzleDiameter = false,
                    ShowBeamDiameter = true,
                    ShowBedSize = true,
                    ShowHasHeatedBed = false,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = false,
                },
                new PrinterCategory()
                {
                    Nickname = "PolyJet",
                    Name = "PolyJet",
                    Description = "Printing with UV curable resin onto a build tray in a process somewhat similar to inkjet printing.",
                    MaterialCategoryNickname = resinCategory.Nickname,
                    ShowFilamentDiameter = false,
                    ShowNozzleDiameter = false,
                    ShowBeamDiameter = false,
                    ShowBedSize = true,
                    ShowHasHeatedBed = false,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = true,
                },
                new PrinterCategory()
                {
                    Nickname = "LCD",
                    Name = "Liquid Crystal Display",
                    Description = "Uses an LCD Screen to mask photosensitive resin.",
                    MaterialCategoryNickname = resinCategory.Nickname,
                    ShowFilamentDiameter = false,
                    ShowNozzleDiameter = false,
                    ShowBeamDiameter = false,
                    ShowBedSize = true,
                    ShowHasHeatedBed = false,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = true,
                },
                new PrinterCategory()
                {
                    Nickname = "DLP",
                    Name = "Digital Light Processing",
                    Description = "Uses a projector or projector-like array to expose a photosensitive resin.",
                    MaterialCategoryNickname = resinCategory.Nickname,
                    ShowFilamentDiameter = false,
                    ShowNozzleDiameter = false,
                    ShowBeamDiameter = false,
                    ShowBedSize = true,
                    ShowHasHeatedBed = false,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = true,
                },
                new PrinterCategory()
                {
                    Nickname = "MSLA",
                    Name = "Micro-stereolithography",
                    Description = "Uses an LED array along with a LCD Photomask to selectively expose a photosensitive resin.",
                    MaterialCategoryNickname = resinCategory.Nickname,
                    ShowFilamentDiameter = false,
                    ShowNozzleDiameter = false,
                    ShowBeamDiameter = false,
                    ShowBedSize = true,
                    ShowHasHeatedBed = false,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = true,
                },
                new PrinterCategory()
                {
                    Nickname = "SLS",
                    Name = "Selective Laser Sintering",
                    Description = "Uses a laser to fuse particles together.",
                    MaterialCategoryNickname = powderCategory.Nickname,
                    ShowFilamentDiameter = false,
                    ShowNozzleDiameter = false,
                    ShowBeamDiameter = true,
                    ShowBedSize = true,
                    ShowHasHeatedBed = false,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = false,
                },
                new PrinterCategory()
                {
                    Nickname = "LPDF",
                    Name = "Laser Powder Bed Fusion",
                    Description = "Generic category for powder based additive manufacturing.",
                    MaterialCategoryNickname = powderCategory.Nickname,
                    ShowFilamentDiameter = false,
                    ShowNozzleDiameter = false,
                    ShowBeamDiameter = true,
                    ShowBedSize = true,
                    ShowHasHeatedBed = false,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = false,
                },
                new PrinterCategory()
                {
                    Nickname = "EBM",
                    Name = "Electron Beam Melting",
                    Description = "Uses an electron beam to fuse particles together.",
                    MaterialCategoryNickname = powderCategory.Nickname,
                    ShowFilamentDiameter = false,
                    ShowNozzleDiameter = false,
                    ShowBeamDiameter = true,
                    ShowBedSize = true,
                    ShowHasHeatedBed = false,
                    ShowHasHeatedChamber = true,
                    ShowScreenResolution = false,
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
                    Name = "Polyamide",
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
                },
                new MaterialType()
                {
                    Id = Guid.Parse("CC3A5FC9-39DD-42C6-8ACC-9C9019DCD307"),
                    Acronym = "Standard Resin",
                    Name = "Standard Resin",
                    DensityGramPerCubicCm = 1.1,
                    MaterialCategoryNickname = resinCategory.Nickname
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

            modelBuilder.Entity<Printer>()
                .Property(p => p.CategoryNickname)
                    .HasDefaultValue(FFFPrinterCategory.Nickname);

            modelBuilder.Entity<Printer>()
                .HasIndex(p => p.Id)
                .IncludeProperties(p => new
                {
                    p.UserId,
                    p.Make,
                    p.Model,
                    p.Description,
                    p.NozzleDiameter,
                    p.FilamentDiameter,
                    p.IsActive,
                    p.Name,
                    p.CategoryNickname,
                    p.BeamDiameter,
                    p.BedDepthMm,
                    p.BedHeightMm,
                    p.BedWidthMm,
                    p.HasHeatedBed,
                    p.HasHeatedChamber,
                    p.ScreenResolutionXPixels,
                    p.ScreenResolutionYPixels
                })
                .HasDatabaseName("IX_Printers_Id_Covering");

            modelBuilder.Entity<Filament>()
                .HasIndex(f => f.Id)
                .IncludeProperties(f => new
                {
                    f.DisplayName,
                    f.Brand,
                    f.MaterialType,
                    f.MaterialCategoryNickname,
                    f.MaterialDensityGramPerCubicCm,
                    f.ColorName,
                    f.ColorHex,
                    f.RecommendedTemp,
                    f.IsActive,
                    f.Notes,
                    f.CreatedDate,
                    f.PurchasePriceValue,
                    f.InitialNominalWeightMg,
                    f.DiameterMm,
                    f.StorageLocation,
                    f.IsFavorite,
                    f.InitialNominalVolumeMl
                })
                .HasDatabaseName("IX_Filaments_Id_Covering");

            modelBuilder.Entity<Print>()
                .HasIndex(p => new { p.CreatedById, p.ViewStatus, p.StartDate, p.CreatedDate })
                .IncludeProperties(p => new { p.Id, p.Title, p.Status, p.PrinterId, p.EstimatedPrintTimeInSeconds, p.PrintTimeInSeconds })
                .HasDatabaseName("IX_Prints_Summary");

            modelBuilder.Entity<PrintFilament>().HasIndex(pf => pf.PrintId).IncludeProperties(pf => new
            {
                pf.FilamentId,
                pf.AmountMg,
                pf.EstimatedAmountMg,
                pf.Notes,
                pf.EstimatedLengthInM,
                pf.LengthInM,
                pf.EstimatedSource,
                pf.EstimatedVolumeMl,
                pf.Source,
                pf.VolumeMl
            });

            modelBuilder.Entity<PrintFilament>()
                .HasIndex(pf => pf.FilamentId)
                .IncludeProperties(pf => new
                {
                    pf.PrintId,
                    pf.EstimatedAmountMg,
                    pf.AmountMg,
                    pf.EstimatedLengthInM,
                    pf.LengthInM,
                    pf.EstimatedVolumeMl,
                    pf.VolumeMl,
                    pf.EstimatedSource,
                    pf.Source,
                    pf.Notes
                })
                .HasDatabaseName("IX_PrintFilament_FilamentId_Covering");

            modelBuilder.Entity<FilamentAdjustment>()
                .HasIndex(fa => fa.FilamentId)
                .IncludeProperties(fa => new
                {
                    fa.AmountMg,
                    fa.VolumeMl,
                    fa.LengthInM,
                    fa.CreatedDate,
                    fa.Notes
                })
                .HasDatabaseName("IX_FilamentAdjustments_FilamentId_Covering");

            modelBuilder.Entity<PrintImage>()
                .HasIndex(pi => pi.PrintId)
                .IncludeProperties(pi => new
                {
                    pi.Id,
                    pi.FileId,
                    pi.IsDefault,
                    pi.CreatedDate,
                    pi.CreatedById,
                    pi.UpdatedDate,
                    pi.UpdatedById
                })
                .HasDatabaseName("IX_PrintImages_PrintId_Default")
                .HasFilter("[IsDefault] = 1");

            modelBuilder.Entity<ProjectImage>()
                .HasIndex(pi => pi.ProjectId)
                .IncludeProperties(pi => new
                {
                    pi.Id,
                    pi.FileId,
                    pi.IsDefault,
                    pi.DisplayOrder,
                    pi.CreatedDate,
                    pi.CreatedById
                })
                .HasDatabaseName("IX_ProjectImages_ProjectId");

            modelBuilder.Entity<Project>()
                .HasIndex(p => new { p.CreatedById, p.CreatedDate })
                .IncludeProperties(p => new { p.Id, p.Name, p.Status, p.ViewStatus, p.Reference })
                .HasDatabaseName("IX_Projects_CreatedById_CreatedDate");

            modelBuilder.Entity<Project>()
                .HasIndex(p => new { p.CreatedById, p.UpdatedDate })
                .IncludeProperties(p => new { p.Id, p.Name, p.Status, p.ViewStatus, p.Reference })
                .HasDatabaseName("IX_Projects_CreatedById_UpdatedDate");

            modelBuilder.Entity<Print>()
                .HasIndex(p => p.ProjectId)
                .HasDatabaseName("IX_Prints_ProjectId");

            // MCP write idempotency: one created entity per (user, tool, key). The unique index is
            // the concurrency guard — a racing duplicate call fails here and is replayed.
            modelBuilder.Entity<McpIdempotencyRecord>()
                .HasIndex(r => new { r.UserId, r.ToolName, r.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("IX_McpIdempotencyRecords_User_Tool_Key");

            modelBuilder.Entity<PrinterMaintenance>().HasIndex(pm => pm.CreatedById).IncludeProperties(pm => new { pm.Date, pm.CreatedDate });

            // Notification indexes for efficient queries
            modelBuilder.Entity<Notification>()
                .HasIndex(n => new { n.UserId, n.IsRead, n.CreatedDate })
                .HasDatabaseName("IX_Notifications_UserId_IsRead_CreatedDate");

            modelBuilder.Entity<Notification>()
                .HasIndex(n => n.UserId)
                .IncludeProperties(n => n.IsRead)
                .HasDatabaseName("IX_Notifications_UserId_UnreadCount");

            modelBuilder.Entity<Subscription>()
                .HasIndex(s => s.UserId)
                .IsUnique()
                .HasDatabaseName("IX_Subscriptions_UserId");

            modelBuilder.Entity<Subscription>()
                .HasIndex(s => s.StripeCustomerId)
                .HasDatabaseName("IX_Subscriptions_StripeCustomerId");

            modelBuilder.Entity<Subscription>()
                .HasIndex(s => s.StripeSubscriptionId)
                .HasDatabaseName("IX_Subscriptions_StripeSubscriptionId");

            // Non-null for the current declaration: fnNaturalSort(string) exists on this type.
            // nameof survives a rename, but not a signature change — altering the parameter list
            // would still compile and return null here, so keep the two in step.
            modelBuilder.HasDbFunction(typeof(PrintLogContext).GetMethod(nameof(fnNaturalSort), new[] { typeof(string) })!)
                .HasName("fnNaturalSort");

            // SQLite doesn't natively support DateTimeOffset comparisons in WHERE clauses.
            // Convert DateTimeOffset to binary (long ticks in UTC) so queries work correctly.
            if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                {
                    var properties = entityType.ClrType.GetProperties()
                        .Where(p => p.PropertyType == typeof(DateTimeOffset) || p.PropertyType == typeof(DateTimeOffset?));
                    foreach (var property in properties)
                    {
                        modelBuilder.Entity(entityType.ClrType)
                            .Property(property.Name)
                            .HasConversion(new DateTimeOffsetToBinaryConverter());
                    }
                }
            }
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
