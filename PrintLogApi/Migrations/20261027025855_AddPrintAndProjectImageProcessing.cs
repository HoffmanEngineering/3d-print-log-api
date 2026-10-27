using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintAndProjectImageProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "ProjectImages",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "ProjectImages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SocialFileId",
                table: "ProjectImages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ThumbnailFileId",
                table: "ProjectImages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "ProjectImages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "PrintImages",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "PrintImages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SocialFileId",
                table: "PrintImages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ThumbnailFileId",
                table: "PrintImages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "PrintImages",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImages_SocialFileId",
                table: "ProjectImages",
                column: "SocialFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImages_ThumbnailFileId",
                table: "ProjectImages",
                column: "ThumbnailFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintImages_SocialFileId",
                table: "PrintImages",
                column: "SocialFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintImages_ThumbnailFileId",
                table: "PrintImages",
                column: "ThumbnailFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_PrintImages_Files_SocialFileId",
                table: "PrintImages",
                column: "SocialFileId",
                principalTable: "Files",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PrintImages_Files_ThumbnailFileId",
                table: "PrintImages",
                column: "ThumbnailFileId",
                principalTable: "Files",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectImages_Files_SocialFileId",
                table: "ProjectImages",
                column: "SocialFileId",
                principalTable: "Files",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectImages_Files_ThumbnailFileId",
                table: "ProjectImages",
                column: "ThumbnailFileId",
                principalTable: "Files",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrintImages_Files_SocialFileId",
                table: "PrintImages");

            migrationBuilder.DropForeignKey(
                name: "FK_PrintImages_Files_ThumbnailFileId",
                table: "PrintImages");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectImages_Files_SocialFileId",
                table: "ProjectImages");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectImages_Files_ThumbnailFileId",
                table: "ProjectImages");

            migrationBuilder.DropIndex(
                name: "IX_ProjectImages_SocialFileId",
                table: "ProjectImages");

            migrationBuilder.DropIndex(
                name: "IX_ProjectImages_ThumbnailFileId",
                table: "ProjectImages");

            migrationBuilder.DropIndex(
                name: "IX_PrintImages_SocialFileId",
                table: "PrintImages");

            migrationBuilder.DropIndex(
                name: "IX_PrintImages_ThumbnailFileId",
                table: "PrintImages");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "ProjectImages");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "ProjectImages");

            migrationBuilder.DropColumn(
                name: "SocialFileId",
                table: "ProjectImages");

            migrationBuilder.DropColumn(
                name: "ThumbnailFileId",
                table: "ProjectImages");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "ProjectImages");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "PrintImages");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "PrintImages");

            migrationBuilder.DropColumn(
                name: "SocialFileId",
                table: "PrintImages");

            migrationBuilder.DropColumn(
                name: "ThumbnailFileId",
                table: "PrintImages");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "PrintImages");
        }
    }
}
