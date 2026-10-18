using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class HistoryRetentionPolicyTests
{
    [Theory]
    [InlineData(0, HistoryRetentionPolicy.MinimumLimit)]
    [InlineData(500, 500)]
    [InlineData(2_001, HistoryRetentionPolicy.MaximumLimit)]
    public void NormalizeLimit_ClampsToSupportedRange(int configured, int expected)
    {
        Assert.Equal(expected, HistoryRetentionPolicy.NormalizeLimit(configured));
    }

    [Fact]
    public void TrimToLimit_RemovesOldestNonFavoritesFirst()
    {
        List<InvocationHistory> history =
        [
            Item("new", favorite: false),
            Item("favorite-new", favorite: true),
            Item("old", favorite: false),
            Item("favorite-old", favorite: true)
        ];

        bool changed = HistoryRetentionPolicy.TrimToLimit(history, 3);

        Assert.True(changed);
        Assert.Equal(["new", "favorite-new", "favorite-old"],
            history.Select(item => item.Id));
    }

    [Fact]
    public void TrimToLimit_RemovesOldestFavoriteOnlyWhenFavoritesFillLimit()
    {
        List<InvocationHistory> history =
        [
            Item("favorite-new", favorite: true),
            Item("favorite-middle", favorite: true),
            Item("favorite-old", favorite: true)
        ];

        HistoryRetentionPolicy.TrimToLimit(history, 2);

        Assert.Equal(["favorite-new", "favorite-middle"],
            history.Select(item => item.Id));
        Assert.All(history, item => Assert.True(item.Favorite));
    }

    private static InvocationHistory Item(string id, bool favorite) => new()
    {
        Id = id,
        Favorite = favorite
    };
}
