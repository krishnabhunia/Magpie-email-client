using Magpie.Core.Mail;

namespace Magpie.Core.Tests;

public sealed class ComposeEmojiCatalogTests
{
    [Fact]
    public void Offline_catalog_has_named_unique_entries_and_categories()
    {
        Assert.True(ComposeEmojiCatalog.Entries.Count >= 40);
        Assert.Equal(ComposeEmojiCatalog.Entries.Count, ComposeEmojiCatalog.Entries.Select(e => e.Value).Distinct().Count());
        Assert.All(ComposeEmojiCatalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.Contains(entry.Category, ComposeEmojiCatalog.Categories);
            Assert.True(ComposeEmojiCatalog.Contains(entry.Value));
        });
    }

    [Theory]
    [InlineData("NAMASTE", "🙏")]
    [InlineData("rupees", "₹")]
    [InlineData("diwali", "🪔")]
    [InlineData("attachment", "📎")]
    [InlineData("check complete", "✅")]
    [InlineData("💻", "💻")]
    public void Search_matches_names_keywords_and_symbols(string query, string expected) =>
        Assert.Contains(ComposeEmojiCatalog.Search(query), entry => entry.Value == expected);

    [Fact]
    public void Category_and_search_filters_combine_without_leaking_other_categories()
    {
        Assert.All(ComposeEmojiCatalog.Search("", "Work"), entry => Assert.Equal("Work", entry.Category));
        Assert.Empty(ComposeEmojiCatalog.Search("namaste", "Work"));
        Assert.Empty(ComposeEmojiCatalog.Search("", "Unknown"));
        Assert.Empty(ComposeEmojiCatalog.Search(new string('x', 1000)));
    }

    [Fact]
    public void Recent_history_is_known_unique_bounded_and_most_recent_first()
    {
        var values = new[] { "unknown", "🙂", "🙂" }.Concat(ComposeEmojiCatalog.Entries.Select(e => e.Value));
        var recent = ComposeEmojiCatalog.Remember(values, "🙏");
        Assert.Equal("🙏", recent[0]);
        Assert.Equal(ComposeEmojiCatalog.MaximumRecent, recent.Count);
        Assert.Equal(recent.Count, recent.Distinct().Count());
        Assert.DoesNotContain("unknown", recent);
        Assert.Equal(recent, ComposeEmojiCatalog.Remember(recent, "<script>"));
    }

    [Fact]
    public void Recent_category_keeps_history_order_and_can_be_searched()
    {
        var recent = new[] { "₹", "🙏", "🙂" };
        Assert.Equal(recent, ComposeEmojiCatalog.Search("", "Recent", recent).Select(e => e.Value));
        Assert.Equal("🙏", Assert.Single(ComposeEmojiCatalog.Search("thanks", "Recent", recent)).Value);
        Assert.Empty(ComposeEmojiCatalog.Search("", "Recent"));
    }
}
