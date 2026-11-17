using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCuraSettingPrintIdForeignKeyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written instead of CreateIndex: production may already have the index (it was
            // created by hand to unblock print deletes before this deployed), and CuraSettings is
            // large (51 GB), so it is built online to keep the table writable meanwhile.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CuraSettings_PrintId_ForeignKey' AND object_id = OBJECT_ID('CuraSettings'))
                    CREATE NONCLUSTERED INDEX [IX_CuraSettings_PrintId_ForeignKey] ON [CuraSettings] ([PrintId]) WITH (ONLINE = ON);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CuraSettings_PrintId_ForeignKey",
                table: "CuraSettings");
        }
    }
}
