using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintSummaryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCK_TIMEOUT 300000;", suppressTransaction: false);
            
            migrationBuilder.DropIndex(
                name: "IX_Prints_CreatedById",
                table: "Prints");

            migrationBuilder.DropIndex(
                name: "IX_PrintImages_PrintId",
                table: "PrintImages");

            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_FilamentId",
                table: "PrintFilament");

            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament");

            migrationBuilder.DropIndex(
                name: "IX_FilamentAdjustments_FilamentId",
                table: "FilamentAdjustments");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Prints_Summary' AND object_id = OBJECT_ID('Prints'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_Prints_Summary] 
                    ON [Prints]([CreatedById], [ViewStatus], [StartDate], [CreatedDate])
                    INCLUDE ([Id], [Title], [Status], [PrinterId], [EstimatedPrintTimeInSeconds], [PrintTimeInSeconds]);
                END
            ");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_PrintImages_PrintId_Default' AND object_id = OBJECT_ID('PrintImages'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_PrintImages_PrintId_Default] 
                    ON [PrintImages]([PrintId])
                    INCLUDE ([Id], [FileId], [IsDefault], [CreatedDate], [CreatedById], [UpdatedDate], [UpdatedById])
                    WHERE [IsDefault] = 1;
                END
            ");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_PrintFilament_FilamentId_Covering' AND object_id = OBJECT_ID('PrintFilament'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_PrintFilament_FilamentId_Covering] 
                    ON [PrintFilament]([FilamentId])
                    INCLUDE ([PrintId], [EstimatedAmountMg], [AmountMg], [EstimatedLengthInM], [LengthInM], [EstimatedVolumeMl], [VolumeMl], [EstimatedSource], [Source], [Notes]);
                END
            ");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_PrintFilament_PrintId' AND object_id = OBJECT_ID('PrintFilament'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_PrintFilament_PrintId] 
                    ON [PrintFilament]([PrintId])
                    INCLUDE ([FilamentId], [AmountMg], [EstimatedAmountMg], [Notes], [EstimatedLengthInM], [LengthInM], [EstimatedSource], [EstimatedVolumeMl], [Source], [VolumeMl]);
                END
            ");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Printers_Id_Covering' AND object_id = OBJECT_ID('Printers'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_Printers_Id_Covering] 
                    ON [Printers]([Id])
                    INCLUDE ([UserId], [Make], [Model], [Description], [NozzleDiameter], [FilamentDiameter], [IsActive], [Name], [CategoryNickname], [BeamDiameter], [BedDepthMm], [BedHeightMm], [BedWidthMm], [HasHeatedBed], [HasHeatedChamber], [ScreenResolutionXPixels], [ScreenResolutionYPixels]);
                END
            ");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Filaments_Id_Covering' AND object_id = OBJECT_ID('Filaments'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_Filaments_Id_Covering] 
                    ON [Filaments]([Id])
                    INCLUDE ([DisplayName], [Brand], [MaterialType], [MaterialCategoryNickname], [MaterialDensityGramPerCubicCm], [ColorName], [ColorHex], [RecommendedTemp], [IsActive], [Notes], [CreatedDate], [PurchasePriceValue], [InitialNominalWeightMg], [DiameterMm], [StorageLocation], [IsFavorite], [InitialNominalVolumeMl]);
                END
            ");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_FilamentAdjustments_FilamentId_Covering' AND object_id = OBJECT_ID('FilamentAdjustments'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_FilamentAdjustments_FilamentId_Covering] 
                    ON [FilamentAdjustments]([FilamentId])
                    INCLUDE ([AmountMg], [VolumeMl], [LengthInM], [CreatedDate], [Notes]);
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCK_TIMEOUT 300000;", suppressTransaction: false);
            
            migrationBuilder.DropIndex(
                name: "IX_Prints_Summary",
                table: "Prints");

            migrationBuilder.DropIndex(
                name: "IX_PrintImages_PrintId_Default",
                table: "PrintImages");

            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_FilamentId_Covering",
                table: "PrintFilament");

            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament");

            migrationBuilder.DropIndex(
                name: "IX_Printers_Id_Covering",
                table: "Printers");

            migrationBuilder.DropIndex(
                name: "IX_Filaments_Id_Covering",
                table: "Filaments");

            migrationBuilder.DropIndex(
                name: "IX_FilamentAdjustments_FilamentId_Covering",
                table: "FilamentAdjustments");

            migrationBuilder.CreateIndex(
                name: "IX_Prints_CreatedById",
                table: "Prints",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_PrintImages_PrintId",
                table: "PrintImages",
                column: "PrintId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_FilamentId",
                table: "PrintFilament",
                column: "FilamentId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament",
                column: "PrintId")
                .Annotation("SqlServer:Include", new[] { "FilamentId", "AmountMg", "EstimatedAmountMg" });

            migrationBuilder.CreateIndex(
                name: "IX_FilamentAdjustments_FilamentId",
                table: "FilamentAdjustments",
                column: "FilamentId");
        }
    }
}
