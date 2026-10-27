using PrintLogApi.SpoolTags.Labels;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class BarcodeValueTests
{
    [Theory]
    [InlineData("849344041764", "0849344041764")]
    [InlineData(" 0849344041764 ", "0849344041764")]
    [InlineData("x004n86wd5", "X004N86WD5")]
    [InlineData("02540924A", "02540924A")]
    [InlineData("1-2604-0403", "1-2604-0403")]
    public void Normalize_CleansValidValues(string input, string expected)
    {
        Assert.Equal(expected, BarcodeValue.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AB<script>")]
    [InlineData("https://bit.ly/abc")]
    public void Normalize_RejectsInvalidValues(string? input)
    {
        Assert.Null(BarcodeValue.Normalize(input));
    }

    [Fact]
    public void Normalize_RejectsValuesOverTheColumnWidth()
    {
        Assert.Null(BarcodeValue.Normalize(new string('A', 65)));
        Assert.Equal(64, BarcodeValue.Normalize(new string('A', 64))!.Length);
    }

    [Theory]
    [InlineData("EAN_13", "0849344041764", "ean13")]
    [InlineData("EAN-13", "0849344041764", "ean13")]
    [InlineData("UPC_A", "0849344041764", "ean13")]
    [InlineData("CODE_128", "X004N86WD5", "code128")]
    [InlineData("Code128", "X004N86WD5", "code128")]
    [InlineData("ean8", "12345670", "ean8")]
    [InlineData("weird", "X004N86WD5", "other")]
    [InlineData(null, "X004N86WD5", "other")]
    public void NormalizeFormat_MapsKnownSymbologies(string? format, string value, string expected)
    {
        Assert.Equal(expected, BarcodeValue.NormalizeFormat(format, value));
    }
}
