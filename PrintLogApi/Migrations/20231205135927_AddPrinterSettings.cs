using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "NozzleDiameter",
                table: "Printers",
                type: "float",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "float");

            migrationBuilder.AlterColumn<double>(
                name: "FilamentDiameter",
                table: "Printers",
                type: "float",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "float");

            migrationBuilder.AddColumn<double>(
                name: "BeamDiameter",
                table: "Printers",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BedDepthMm",
                table: "Printers",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BedHeightMm",
                table: "Printers",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BedWidthMm",
                table: "Printers",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasHeatedBed",
                table: "Printers",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasHeatedChamber",
                table: "Printers",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ScreenResolutionXPixels",
                table: "Printers",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ScreenResolutionYPixels",
                table: "Printers",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowBeamDiameter",
                table: "PrinterCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowBedSize",
                table: "PrinterCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowFilamentDiameter",
                table: "PrinterCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowHasHeatedBed",
                table: "PrinterCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowHasHeatedChamber",
                table: "PrinterCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowNozzleDiameter",
                table: "PrinterCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowScreenResolution",
                table: "PrinterCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "DLP",
                columns: new[] { "Description", "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { "Uses a projector or projector-like array to expose a photosensitive resin.", false, true, false, false, true, false, true });

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "EBM",
                columns: new[] { "Description", "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { "Uses an electron beam to fuse particles together.", true, true, false, false, true, false, false });

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "FDM",
                columns: new[] { "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { false, true, true, true, true, true, false });

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "FFF",
                columns: new[] { "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { false, true, true, true, true, true, false });

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "LCD",
                columns: new[] { "Description", "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { "Uses an LCD Screen to mask photosensitive resin.", false, true, false, false, true, false, true });

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "LPDF",
                columns: new[] { "Description", "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { "Generic category for powder based additive manufacturing.", true, true, false, false, true, false, false });

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "MSLA",
                columns: new[] { "Description", "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { "Uses an LED array along with a LCD Photomask to selectively expose a photosensitive resin.", false, true, false, false, true, false, true });

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "SLA",
                columns: new[] { "Description", "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { "Laser based SLA which cures photosensitive resin.", true, true, false, false, true, false, false });

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "SLS",
                columns: new[] { "Description", "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { "Uses a laser to fuse particles together.", true, true, false, false, true, false, false });

            migrationBuilder.InsertData(
                table: "PrinterCategories",
                columns: new[] { "Nickname", "Description", "MaterialCategoryNickname", "Name", "ShowBeamDiameter", "ShowBedSize", "ShowFilamentDiameter", "ShowHasHeatedBed", "ShowHasHeatedChamber", "ShowNozzleDiameter", "ShowScreenResolution" },
                values: new object[] { "PolyJet", "Printing with UV curable resin onto a build tray in a process somewhat similar to inkjet printing.", "resin", "PolyJet", false, true, false, false, true, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "PolyJet");

            migrationBuilder.DropColumn(
                name: "BeamDiameter",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "BedDepthMm",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "BedHeightMm",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "BedWidthMm",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "HasHeatedBed",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "HasHeatedChamber",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "ScreenResolutionXPixels",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "ScreenResolutionYPixels",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "ShowBeamDiameter",
                table: "PrinterCategories");

            migrationBuilder.DropColumn(
                name: "ShowBedSize",
                table: "PrinterCategories");

            migrationBuilder.DropColumn(
                name: "ShowFilamentDiameter",
                table: "PrinterCategories");

            migrationBuilder.DropColumn(
                name: "ShowHasHeatedBed",
                table: "PrinterCategories");

            migrationBuilder.DropColumn(
                name: "ShowHasHeatedChamber",
                table: "PrinterCategories");

            migrationBuilder.DropColumn(
                name: "ShowNozzleDiameter",
                table: "PrinterCategories");

            migrationBuilder.DropColumn(
                name: "ShowScreenResolution",
                table: "PrinterCategories");

            migrationBuilder.AlterColumn<double>(
                name: "NozzleDiameter",
                table: "Printers",
                type: "float",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "float",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "FilamentDiameter",
                table: "Printers",
                type: "float",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "float",
                oldNullable: true);

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "DLP",
                column: "Description",
                value: "");

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "EBM",
                column: "Description",
                value: "");

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "LCD",
                column: "Description",
                value: "");

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "LPDF",
                column: "Description",
                value: "");

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "MSLA",
                column: "Description",
                value: "");

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "SLA",
                column: "Description",
                value: "");

            migrationBuilder.UpdateData(
                table: "PrinterCategories",
                keyColumn: "Nickname",
                keyValue: "SLS",
                column: "Description",
                value: "");
        }
    }
}
