using PrintLogApi.Exceptions;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.SpoolTags;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class TagReadingSanitizerTests
{
    [Theory]
    [InlineData(520, null, true)]
    [InlineData(501, null, true)]
    [InlineData(0, null, false)]
    [InlineData(500, 500, false)]
    [InlineData(250, 250, false)]
    public void Hotend_IsRangeChecked(int input, int? expected, bool warns)
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading { HotendMaxC = input });

        Assert.Equal(expected, result.HotendMaxC);
        Assert.Equal(warns, result.Warnings.Contains("hotend-out-of-range"));
        if (!warns)
        {
            Assert.Empty(result.Warnings);
        }
    }

    [Fact]
    public void FormatAndSku_AreCappedToTheirColumnWidths()
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading { Format = new string('f', 255), Sku = new string('9', 255) });

        Assert.True(result.Format!.Length <= ColumnWidth<PrintLogApi.Models.FilamentTag>(nameof(PrintLogApi.Models.FilamentTag.Format)));
        Assert.True(result.Format.Length <= ColumnWidth<PrintLogApi.Models.FilamentCatalogSku>(nameof(PrintLogApi.Models.FilamentCatalogSku.Format)));
        Assert.True(result.Sku!.Length <= ColumnWidth<PrintLogApi.Models.FilamentCatalogSku>(nameof(PrintLogApi.Models.FilamentCatalogSku.Sku)));
    }

    // SQLite ignores column lengths, so the suite cannot see an over-long value; compare to the model instead.
    internal static int ColumnWidth<T>(string property) =>
        ((System.ComponentModel.DataAnnotations.MaxLengthAttribute)Attribute.GetCustomAttribute(
            typeof(T).GetProperty(property)!, typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute))!).Length;

    [Fact]
    public void AllHotendFields_AreChecked()
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading
        {
            HotendMinC = 600,
            HotendMaxC = 600,
            FirstLayerHotendC = 600,
            HotendC = 600,
        });

        Assert.Null(result.HotendMinC);
        Assert.Null(result.HotendMaxC);
        Assert.Null(result.FirstLayerHotendC);
        Assert.Null(result.HotendC);
        Assert.Contains("hotend-out-of-range", result.Warnings);
    }

    [Theory]
    [InlineData(501, null, "bed-out-of-range")]
    [InlineData(0, null, null)]
    [InlineData(60, 60, null)]
    public void Bed_IsRangeChecked(int input, int? expected, string? warning)
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading { BedMinC = input, BedMaxC = input });

        Assert.Equal(expected, result.BedMinC);
        Assert.Equal(expected, result.BedMaxC);
        AssertWarning(result, warning);
    }

    [Theory]
    [InlineData(10001, null, "weight-out-of-range")]
    [InlineData(0, null, null)]
    [InlineData(1000, 1000, null)]
    [InlineData(10000, 10000, null)]
    public void NetWeight_IsRangeChecked(int input, int? expected, string? warning)
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading { NetWeightG = input });

        Assert.Equal(expected, result.NetWeightG);
        AssertWarning(result, warning);
    }

    [Theory]
    [InlineData(10001, null, "length-out-of-range")]
    [InlineData(0, null, null)]
    [InlineData(330, 330, null)]
    public void Length_IsRangeChecked(int input, int? expected, string? warning)
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading { LengthM = input });

        Assert.Equal(expected, result.LengthM);
        AssertWarning(result, warning);
    }

    [Theory]
    [InlineData(1.7, null, "diameter-unsupported")]
    [InlineData(0.0, null, null)]
    [InlineData(1.75, 1.75, null)]
    [InlineData(1.751, 1.751, null)]
    [InlineData(2.85, 2.85, null)]
    [InlineData(3.0, 3.0, null)]
    public void Diameter_MustBeASupportedSize(double input, double? expected, string? warning)
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading { DiameterMm = input });

        if (expected is null)
        {
            Assert.Null(result.DiameterMm);
        }
        else
        {
            Assert.NotNull(result.DiameterMm);
            Assert.Equal(expected.Value, result.DiameterMm.Value, 0.01);
        }
        AssertWarning(result, warning);
    }

    [Theory]
    [InlineData(49, null)]
    [InlineData(48, 48)]
    [InlineData(0, null)]
    public void DryHours_AreRangeChecked(int input, int? expected)
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading { DryHours = input });

        Assert.Equal(expected, result.DryHours);
        AssertWarning(result, input > 48 ? "dry-out-of-range" : null);
    }

    [Theory]
    [InlineData(151, null)]
    [InlineData(150, 150)]
    [InlineData(0, null)]
    public void DryTemp_IsRangeChecked(int input, int? expected)
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading { DryTempC = input });

        Assert.Equal(expected, result.DryTempC);
        AssertWarning(result, input > 150 ? "dry-out-of-range" : null);
    }

    [Fact]
    public void ZeroMeansNotSet_OnEveryNumericField()
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading
        {
            MaterialSubtypeCode = 0,
            Alpha = 0,
            DiameterMm = 0,
            NetWeightG = 0,
            LengthM = 0,
            HotendMinC = 0,
            HotendMaxC = 0,
            FirstLayerHotendC = 0,
            HotendC = 0,
            BedMinC = 0,
            BedMaxC = 0,
            DryTempC = 0,
            DryHours = 0,
        });

        Assert.Null(result.MaterialSubtypeCode);
        Assert.Null(result.Alpha);
        Assert.Null(result.DiameterMm);
        Assert.Null(result.NetWeightG);
        Assert.Null(result.LengthM);
        Assert.Null(result.HotendMinC);
        Assert.Null(result.HotendMaxC);
        Assert.Null(result.FirstLayerHotendC);
        Assert.Null(result.HotendC);
        Assert.Null(result.BedMinC);
        Assert.Null(result.BedMaxC);
        Assert.Null(result.DryTempC);
        Assert.Null(result.DryHours);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Alpha_OutsideByteRange_IsDropped()
    {
        Assert.Null(TagReadingSanitizer.Sanitize(new TagReading { Alpha = 256 }).Alpha);
        Assert.Equal(255, TagReadingSanitizer.Sanitize(new TagReading { Alpha = 255 }).Alpha);
    }

    [Fact]
    public void Colors_AreNormalizedFilteredAndCapped()
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading
        {
            Colors = ["#ff0000", "00ff00", "GGGGGG", "12345", "1234567", "#0000FF", "", "AAAAAA", "BBBBBB", "CCCCCC", "DDDDDD"],
        });

        Assert.Equal(["FF0000", "00FF00", "0000FF", "AAAAAA", "BBBBBB"], result.Colors);
    }

    [Fact]
    public void Strings_AreTrimmedAndCapped()
    {
        var result = TagReadingSanitizer.Sanitize(new TagReading
        {
            Brand = "  " + new string('b', 300) + "  ",
            Manufacturer = new string('m', 300),
            MaterialType = new string('t', 300),
            MaterialSubtype = new string('s', 300),
            Sku = "  " + new string('k', 100),
        });

        Assert.Equal(255, result.Brand!.Length);
        Assert.Equal(255, result.Manufacturer!.Length);
        Assert.Equal(255, result.MaterialType!.Length);
        Assert.Equal(255, result.MaterialSubtype!.Length);
        Assert.Equal(64, result.Sku!.Length);
        Assert.Equal('b', result.Brand[0]);
    }

    [Fact]
    public void Extras_DropOversizedAndExcessEntries()
    {
        var extras = new Dictionary<string, string>
        {
            [new string('k', 51)] = "v",
            ["bigValue"] = new string('v', 201),
        };
        for (var i = 0; i < 25; i++)
        {
            extras["key" + i] = "value" + i;
        }

        var result = TagReadingSanitizer.Sanitize(new TagReading { Extras = extras });

        Assert.Equal(20, result.Extras.Count);
        Assert.DoesNotContain("bigValue", result.Extras.Keys);
        Assert.All(result.Extras, kv => Assert.StartsWith("key", kv.Key));
    }

    [Fact]
    public void Sanitize_ReturnsANewInstance_AndCopesWithNullCollections()
    {
        var input = new TagReading { Colors = null!, Extras = null!, Warnings = null!, Format = "x", DecoderVersion = 3 };

        var result = TagReadingSanitizer.Sanitize(input);

        Assert.NotSame(input, result);
        Assert.Equal("x", result.Format);
        Assert.Equal(3, result.DecoderVersion);
        Assert.Empty(result.Colors);
        Assert.Empty(result.Extras);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Sanitize_KeepsDecoderWarnings_WithoutDuplicates()
    {
        var input = new TagReading { Warnings = ["decoder-note"], BedMaxC = 900, BedMinC = 900 };

        var result = TagReadingSanitizer.Sanitize(input);

        Assert.Contains("decoder-note", result.Warnings);
        Assert.Single(result.Warnings, w => w == "bed-out-of-range");
    }

    [Fact]
    public void Serialize_ReturnsNull_WhenOverTheSizeCap()
    {
        var reading = new TagReading { Extras = new Dictionary<string, string> { ["a"] = new string('x', 8 * 1024) } };

        Assert.Null(TagReadingJson.Serialize(reading));
    }

    [Fact]
    public void Json_RoundTrips_OmitsNulls_AndUsesCamelCase()
    {
        var reading = new TagReading
        {
            Brand = "Acme",
            Colors = ["FF0000"],
            ManufacturedOn = new DateOnly(2026, 3, 4),
        };

        var json = TagReadingJson.Serialize(reading);

        Assert.NotNull(json);
        Assert.Contains("\"brand\":\"Acme\"", json);
        Assert.DoesNotContain("sku", json);
        var back = TagReadingJson.Deserialize(json);
        Assert.NotNull(back);
        Assert.Equal("Acme", back.Brand);
        Assert.Equal(["FF0000"], back.Colors);
        Assert.Equal(new DateOnly(2026, 3, 4), back.ManufacturedOn);
        Assert.Null(TagReadingJson.Deserialize(null));
        Assert.Null(TagReadingJson.Deserialize(""));
    }

    [Fact]
    public void Parse_NullDto_ReturnsNull()
    {
        Assert.Null(TagMemory.Parse(null));
    }

    [Theory]
    [InlineData("classic", 1024, true)]
    [InlineData("classic", 1023, false)]
    [InlineData("type2", 1024, true)]
    [InlineData("type2", 1028, false)]
    [InlineData("type2", 0, false)]
    [InlineData("nfcv", 2048, true)]
    [InlineData("nfcv", 2049, false)]
    [InlineData("bogus", 16, false)]
    [InlineData(null, 16, false)]
    public void Parse_EnforcesKindAndSize(string? kind, int length, bool ok)
    {
        var dto = new TagMemoryDto { Kind = kind, Data = Convert.ToBase64String(new byte[length]) };

        if (ok)
        {
            var memory = TagMemory.Parse(dto);
            Assert.NotNull(memory);
            Assert.Equal(length, memory.Data.Length);
            Assert.Empty(memory.Unreadable);
        }
        else
        {
            Assert.Throws<BadRequestException>(() => TagMemory.Parse(dto));
        }
    }

    [Fact]
    public void Parse_BadBase64_Throws()
    {
        Assert.Throws<BadRequestException>(() => TagMemory.Parse(new TagMemoryDto { Kind = "classic", Data = "%%%" }));
        Assert.Throws<BadRequestException>(() => TagMemory.Parse(new TagMemoryDto { Kind = "classic", Data = null }));
    }

    [Fact]
    public void Parse_MapsKindAndPassesThroughFields()
    {
        var memory = TagMemory.Parse(new TagMemoryDto
        {
            Kind = "NFCV",
            Data = Convert.ToBase64String(new byte[64]),
            KeyFormat = "fmt",
            Unreadable = [3, 4],
        });

        Assert.NotNull(memory);
        Assert.Equal(TagMemoryKind.NfcV, memory.Kind);
        Assert.Equal("fmt", memory.KeyFormat);
        Assert.Equal([3, 4], memory.Unreadable);
    }

    private static void AssertWarning(TagReading result, string? warning)
    {
        if (warning is null)
        {
            Assert.Empty(result.Warnings);
        }
        else
        {
            Assert.Contains(warning, result.Warnings);
        }
    }
}
