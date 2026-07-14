using PrintLogApi;
using Xunit;

namespace PrintLogApi.IntegrationTests
{
    public class PrintMetricsTests
    {
        [Theory]
        // actual, estimated, expected value, expected isEstimated
        [InlineData(null, 6933, 6933, true)]   // production print 402378: never completed, real estimate
        [InlineData(0, 3600, 3600, true)]      // a webhook's coerced 0 must NOT suppress the estimate
        [InlineData(-5, 3600, 3600, true)]     // negative is corrupt, not a duration
        [InlineData(7200, 3600, 7200, false)]  // a real actual always wins
        [InlineData(7200, null, 7200, false)]
        [InlineData(null, null, 0, false)]     // nothing recorded: 0, and NOT flagged estimated
        [InlineData(null, 0, 0, false)]        // Moonraker's hardcoded 0 is not an estimate
        [InlineData(0, 0, 0, false)]
        [InlineData(0, -5, 0, false)]
        public void Resolve_AppliesTheRule(int? actual, int? estimated, int expectedValue, bool expectedIsEstimated)
        {
            Assert.Equal(expectedValue, PrintMetrics.Resolve(actual, estimated));
            Assert.Equal(expectedIsEstimated, PrintMetrics.IsEstimated(actual, estimated));
        }
    }
}
