using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    /// <summary>
    /// Drives the shared corpus in tests-fixtures/cost-fixtures.json. The Jasmine spec in the UI
    /// repo drives the same file, so a divergence between the two implementations fails both suites.
    /// </summary>
    public class PrintCostCalculatorTests
    {
        private sealed record Fixture(CostInputsDto Inputs, List<FilamentCase> FilamentCases, List<ElectricityCase> ElectricityCases);
        private sealed record CostInputsDto(string UserCurrency, string DefaultFilamentPrice, string KwhRate, string DefaultWattageW);
        private sealed record FilamentCase(string Name, List<FilamentCostRow> Rows, decimal? ExpectedAmount, bool ExpectedUsedDefaultPrice, List<string> ExpectedExclusions);
        private sealed record ElectricityCase(string Name, int DurationSeconds, double? PrinterWattageW, decimal? ExpectedAmount, List<string> ExpectedExclusions);

        private static Fixture Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "cost-fixtures.json");
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Fixture>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            });
        }

        public static IEnumerable<object[]> FilamentCaseNames() =>
            Load().FilamentCases.Select(c => new object[] { c.Name });

        [Theory]
        [MemberData(nameof(FilamentCaseNames))]
        public void FilamentCost_MatchesTheGoldenCorpus(string caseName)
        {
            var fixture = Load();
            var c = fixture.FilamentCases.Single(x => x.Name == caseName);
            var inputs = new CostInputs(fixture.Inputs.UserCurrency, fixture.Inputs.DefaultFilamentPrice,
                fixture.Inputs.KwhRate, fixture.Inputs.DefaultWattageW);

            var result = PrintCostCalculator.FilamentCost(c.Rows, inputs);

            Assert.Equal(c.ExpectedAmount, result.Amount);
            Assert.Equal(c.ExpectedUsedDefaultPrice, result.UsedDefaultPrice);
            Assert.Equal(c.ExpectedExclusions.OrderBy(x => x), result.ExclusionReasons.OrderBy(x => x));
        }

        public static IEnumerable<object[]> ElectricityCaseNames() =>
            Load().ElectricityCases.Select(c => new object[] { c.Name });

        [Theory]
        [MemberData(nameof(ElectricityCaseNames))]
        public void ElectricityCost_MatchesTheGoldenCorpus(string caseName)
        {
            var fixture = Load();
            var c = fixture.ElectricityCases.Single(x => x.Name == caseName);
            var inputs = new CostInputs(fixture.Inputs.UserCurrency, fixture.Inputs.DefaultFilamentPrice,
                fixture.Inputs.KwhRate, fixture.Inputs.DefaultWattageW);

            var result = PrintCostCalculator.ElectricityCost(c.DurationSeconds, c.PrinterWattageW, inputs);

            Assert.Equal(c.ExpectedAmount, result.Amount);
            Assert.Equal(c.ExpectedExclusions.OrderBy(x => x), result.ExclusionReasons.OrderBy(x => x));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("abc", null)]
        [InlineData("-5", null)]
        [InlineData("1,5", null)]
        [InlineData("1.5", 1.5)]
        [InlineData("0", 0.0)]
        public void ParseInvariant_RejectsNonNumericNegativeAndLocaleFormattedValues(string raw, double? expected)
        {
            Assert.Equal((decimal?)expected, PrintCostCalculator.ParseInvariant(raw));
        }

        [Fact]
        public void ElectricityCost_WithoutARateIsExcludedRatherThanZero()
        {
            var inputs = new CostInputs("USD", "20.00", null, "120");
            var result = PrintCostCalculator.ElectricityCost(7200, 120, inputs);

            Assert.Null(result.Amount);
            Assert.Contains(ExclusionReason.RateMissing, result.ExclusionReasons);
        }

        [Fact]
        public void ElectricityCost_WithoutAnyWattageIsExcludedRatherThanZero()
        {
            var inputs = new CostInputs("USD", "20.00", "0.15", null);
            var result = PrintCostCalculator.ElectricityCost(7200, null, inputs);

            Assert.Null(result.Amount);
            Assert.Contains(ExclusionReason.WattageMissing, result.ExclusionReasons);
        }
    }
}
