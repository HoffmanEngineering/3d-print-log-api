using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>
/// Input-bound printer rules. Everything needing the database (category existence, ownership)
/// lives in PrinterService; everything checkable from the arguments alone lives here, so
/// create_printer and update_printer cannot drift apart.
/// </summary>
public class McpPrinterValidationTests
{
    private static PrinterAttributesInput Valid() => new()
    {
        Make = "Bambu",
        Model = "X1C",
        Name = "Workshop X1C",
    };

    private static string CodeOf(PrinterAttributesInput input)
    {
        var ex = Assert.Throws<McpToolException>(() => McpPrinterValidation.ValidateAttributes(input));
        return ex.Code;
    }

    [Fact]
    public void ValidateAttributes_AcceptsAMinimalPrinter()
    {
        McpPrinterValidation.ValidateAttributes(Valid());
    }

    [Fact]
    public void ValidateAttributes_RejectsOverLongMake()
    {
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { Make = new string('x', 51) }));
    }

    [Fact]
    public void ValidateAttributes_RejectsOverLongModel()
    {
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { Model = new string('x', 51) }));
    }

    [Fact]
    public void ValidateAttributes_RejectsOverLongName()
    {
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { Name = new string('x', 101) }));
    }

    [Fact]
    public void ValidateAttributes_RejectsOverLongDescription()
    {
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { Description = new string('x', 1001) }));
    }

    [Fact]
    public void ValidateAttributes_RejectsOverLongCategoryNickname()
    {
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { CategoryNickname = new string('x', 51) }));
    }

    // A blank make/model/name is a whitespace-only string that survives [Required] on the DTO
    // but means nothing. MCP writes always set real values.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAttributes_RejectsBlankIdentityFields(string blank)
    {
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { Make = blank }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { Model = blank }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { Name = blank }));
    }

    // A stored NaN or -1 would corrupt every later reading of these dimensions, and neither is
    // rejected by the entity's column type.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public void ValidateAttributes_RejectsNonFiniteOrNegativeNumerics(double bad)
    {
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { NozzleDiameterMm = bad }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { FilamentDiameterMm = bad }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { BeamDiameterMm = bad }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { BedWidthMm = bad }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { BedDepthMm = bad }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { BedHeightMm = bad }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { ScreenResolutionXPixels = bad }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { ScreenResolutionYPixels = bad }));
        Assert.Equal("invalid_arguments", CodeOf(Valid() with { WattageW = bad }));
    }

    // Zero is legitimate: an unset bed height, a 0 W idle draw.
    [Fact]
    public void ValidateAttributes_AcceptsZeroNumerics()
    {
        McpPrinterValidation.ValidateAttributes(Valid() with { NozzleDiameterMm = 0, WattageW = 0 });
    }

    [Fact]
    public void Canonicalize_TrimsStringsAndPreservesNull()
    {
        var c = new PrinterAttributesInput
        {
            Make = "  Bambu  ",
            Model = "  X1C  ",
            Name = "  Workshop  ",
            Description = "  desc  ",
            CategoryNickname = "  FFF  ",
        }.Canonicalize();

        Assert.Equal("Bambu", c.Make);
        Assert.Equal("X1C", c.Model);
        Assert.Equal("Workshop", c.Name);
        Assert.Equal("desc", c.Description);
        Assert.Equal("FFF", c.CategoryNickname);
        Assert.Null(new PrinterAttributesInput().Canonicalize().Make);
    }

    [Fact]
    public void RequireClearableFields_AcceptsEveryDeclaredField()
    {
        McpPrinterValidation.RequireClearableFields(new HashSet<string>(McpPrinterValidation.ClearableFields));
    }

    // Identity and the category are not clearable: a printer with no make, or no category, is
    // not a state MCP will create.
    [Theory]
    [InlineData("make")]
    [InlineData("model")]
    [InlineData("name")]
    [InlineData("isActive")]
    [InlineData("categoryNickname")]
    [InlineData("nozzleDiameter")] // near-miss typo for nozzleDiameterMm
    public void RequireClearableFields_RejectsNonClearableFields(string field)
    {
        var ex = Assert.Throws<McpToolException>(
            () => McpPrinterValidation.RequireClearableFields(new HashSet<string> { field }));
        Assert.Equal("invalid_arguments", ex.Code);
    }
}
