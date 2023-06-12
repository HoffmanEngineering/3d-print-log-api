using Microsoft.EntityFrameworkCore.Migrations;
using PrintLogApi.Models;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentMeasurementSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InertGas",
                table: "Filaments",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "InitialLayerTimeS",
                table: "Filaments",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InitialNominalVolumeMl",
                table: "Filaments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<double>(
                name: "LayerTimeS",
                table: "Filaments",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MeltingTemperature",
                table: "Filaments",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaterialRefreshRatio",
                table: "Filaments",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Filaments",
                type: "int",
                nullable: false,
                defaultValue: Filament.SourceMeasurement.Weight);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InertGas",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "InitialLayerTimeS",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "InitialNominalVolumeMl",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "LayerTimeS",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "MeltingTemperature",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "MaterialRefreshRatio",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Filaments");
        }
    }
}
