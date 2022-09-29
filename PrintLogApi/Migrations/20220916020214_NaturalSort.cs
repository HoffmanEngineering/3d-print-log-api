using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    public partial class NaturalSort : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
-- =================================================
-- Author:      Theodore Brown
-- Create date: 2016-12-02
-- Description: Sort alphanumeric strings naturally!
-- =================================================
CREATE FUNCTION [dbo].[fnNaturalSort]
(
    @string nvarchar(255)
)
RETURNS nvarchar(264)
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @sortString nvarchar(264);
    DECLARE @startIndex int, @endIndex int;
    DECLARE @afterStartIndex nvarchar(255);
    DECLARE @firstNum varchar(10); -- max length of int

    SELECT @startIndex = PATINDEX('%[0-9]%', @string);
    SELECT @afterStartIndex = SUBSTRING(@string, @startIndex, LEN(@string));
    SELECT @endIndex = PATINDEX('%[^0-9]%', @afterStartIndex) - 1;

    SELECT @firstNum =
        CASE
            WHEN @endIndex < 0 THEN @afterStartIndex -- rest of string after start index is number
            ELSE SUBSTRING(@afterStartIndex, 1, @endIndex)
        END;

    SELECT @sortString =
        CASE
            WHEN LEN(@firstNum) = 0 THEN @string
            -- padd first number to 10 digits and replace it in the string
            ELSE STUFF(@string, @startIndex, LEN(@firstNum), REPLICATE('0', 10 - LEN(@firstNum)) + @firstNum)
        END;

    RETURN @sortString;
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS [dbo].[fnNaturalSort]");
        }
    }
}
