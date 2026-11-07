using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCuraSettingsUnlinkedCreatedDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written instead of CreateIndex, like AddCuraSettingPrintIdForeignKeyIndex:
            // CuraSettings is large (52 GB before the retention job's first run), so the index is
            // built online to keep the table writable, and only if missing, so it can be created by
            // hand first. The build reads only the in-row columns (the Settings JSON is off-row),
            // a few hundred MB, well inside the bundle's 900 s command timeout.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CuraSettings_Unlinked_CreatedDate' AND object_id = OBJECT_ID('CuraSettings'))
                    CREATE NONCLUSTERED INDEX [IX_CuraSettings_Unlinked_CreatedDate] ON [CuraSettings] ([CreatedDate]) WHERE [PrintId] IS NULL WITH (ONLINE = ON);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CuraSettings_Unlinked_CreatedDate",
                table: "CuraSettings");
        }
    }
}
