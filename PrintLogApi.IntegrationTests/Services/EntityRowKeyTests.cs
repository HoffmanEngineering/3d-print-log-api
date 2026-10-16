using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The key UpdateTimestamps matches a Modified entry to the Deleted row it replaces by (#256).
/// </summary>
public class EntityRowKeyTests
{
    [Fact]
    public void CompositeKeysThatJoinToTheSameString_AreDifferentRows()
    {
        var left = new EntityRowKey(typeof(FilamentAdjustment), ["a|b", "c"]);
        var right = new EntityRowKey(typeof(FilamentAdjustment), ["a", "b|c"]);

        Assert.NotEqual(left, right);

        var index = EntityRowKey.IndexFirst([(left, 1), (right, 2)]);
        Assert.Equal(1, index[left]);
        Assert.Equal(2, index[right]);
    }

    [Fact]
    public void EqualValues_AreTheSameRow_AndADuplicateKeepsTheFirstWithoutThrowing()
    {
        var id = Guid.NewGuid();
        var first = new EntityRowKey(typeof(FilamentAdjustment), [id]);
        var second = new EntityRowKey(typeof(FilamentAdjustment), [id]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        var index = EntityRowKey.IndexFirst([(first, 1), (second, 2)]);
        Assert.Equal(1, Assert.Single(index).Value);
    }

    [Fact]
    public void SameKeyValues_OnDifferentTables_AreDifferentRows()
    {
        var id = Guid.NewGuid();

        Assert.NotEqual(
            new EntityRowKey(typeof(FilamentAdjustment), [id]),
            new EntityRowKey(typeof(Filament), [id]));
    }
}
