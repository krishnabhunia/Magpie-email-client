using System.Text.Json;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

public sealed class ComposeToolbarSettingsTests
{
    private static readonly string[] ExpectedIds =
    {
        "attach", "recentFiles", "fontFamily", "fontSize", "bold", "italic", "underline", "strikeThrough",
        "foreColor", "hiliteColor", "insertUnorderedList", "insertOrderedList", "justifyLeft", "justifyCenter",
        "indent", "outdent", "link", "removeLink", "table", "image", "resizeImage", "emoji", "undo", "redo",
        "find", "more", "removeFormat",
    };

    [Fact]
    public void Defaults_have_all_stable_commands_in_the_approved_order()
    {
        Assert.Equal(ExpectedIds, ComposeToolbarCatalog.Definitions.Select(definition => definition.Id));
        Assert.Equal(ExpectedIds, ComposeToolbarCatalog.DefaultButtons().Select(button => button.Id));
        Assert.All(ComposeToolbarCatalog.DefaultButtons(), button => Assert.True(button.Visible));
        Assert.Equal(new[] { "attach", "more" }, ComposeToolbarCatalog.Definitions.Where(definition => definition.Required).Select(definition => definition.Id));
        Assert.All(ComposeToolbarCatalog.Definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Name));
            Assert.False(string.IsNullOrWhiteSpace(definition.Group));
        });
    }

    [Fact]
    public void Defaults_and_catalog_cannot_share_mutable_button_state()
    {
        var first = ComposeToolbarCatalog.DefaultButtons();
        first[0].Id = "changed";
        first[1].Visible = false;
        var second = ComposeToolbarCatalog.DefaultButtons();
        Assert.Equal("attach", second[0].Id);
        Assert.True(second[1].Visible);
        Assert.Equal("attach", ComposeToolbarCatalog.Definitions[0].Id);
        Assert.Throws<NotSupportedException>(() => ((IList<ComposeToolbarDefinition>)ComposeToolbarCatalog.Definitions)
            .Add(new ComposeToolbarDefinition("external", "External", "Unknown", false)));
    }

    [Fact]
    public void Normalise_keeps_first_explicit_visibility_and_rejects_invalid_ids()
    {
        var input = new List<ToolbarButton>
        {
            new() { Id = "emoji", Visible = false },
            null!,
            new() { Id = null!, Visible = true },
            new() { Id = "bold", Visible = false },
            new() { Id = "emoji", Visible = true },
            new() { Id = "bold", Visible = true },
            new() { Id = "Bold", Visible = true },
            new() { Id = "unknown", Visible = true },
            new() { Id = "", Visible = true },
            new() { Id = "attach", Visible = false },
            new() { Id = "more", Visible = false },
        };
        var normalized = ComposeToolbarCatalog.Normalise(input);
        Assert.Equal(ExpectedIds.Length, normalized.Count);
        Assert.Equal(new[] { "emoji", "bold", "attach", "more" }, normalized.Take(4).Select(button => button.Id));
        Assert.False(normalized[0].Visible);
        Assert.False(normalized[1].Visible);
        Assert.True(normalized[2].Visible);
        Assert.True(normalized[3].Visible);
        Assert.Equal(ExpectedIds.Except(new[] { "emoji", "bold", "attach", "more" }), normalized.Skip(4).Select(button => button.Id));
        Assert.All(normalized.Skip(4), button => Assert.True(button.Visible));
        normalized[0].Visible = true;
        Assert.False(input[0].Visible);
    }

    [Fact]
    public void Null_and_empty_input_restore_visible_defaults()
    {
        var fromNull = ComposeToolbarCatalog.Normalise(null);
        var fromEmpty = ComposeToolbarCatalog.Normalise(Array.Empty<ToolbarButton>());
        Assert.Equal(ExpectedIds, fromNull.Select(button => button.Id));
        Assert.Equal(ExpectedIds, fromEmpty.Select(button => button.Id));
        Assert.All(fromNull.Concat(fromEmpty), button => Assert.True(button.Visible));
    }

    [Fact]
    public void Normalise_is_bounded_even_for_an_unbounded_source()
    {
        var read = 0;
        IEnumerable<ToolbarButton> Source()
        {
            for (var index = 0; index < ComposeToolbarCatalog.MaximumInputEntries; index++)
            {
                read++;
                yield return new ToolbarButton { Id = index == 0 ? "emoji" : "unknown", Visible = false };
            }
            throw new InvalidOperationException("The bounded reader must not ask for another entry.");
        }
        var normalized = ComposeToolbarCatalog.Normalise(Source());
        Assert.Equal(ComposeToolbarCatalog.MaximumInputEntries, read);
        Assert.Equal("emoji", normalized[0].Id);
        Assert.False(normalized[0].Visible);
        Assert.Equal(ExpectedIds.Length, normalized.Count);
    }

    [Fact]
    public void Normalise_is_idempotent_and_does_not_reuse_input_objects()
    {
        var once = ComposeToolbarCatalog.Normalise(new[] { new ToolbarButton { Id = "fontSize", Visible = false } });
        var twice = ComposeToolbarCatalog.Normalise(once);
        Assert.Equal(once.Select(button => (button.Id, button.Visible)), twice.Select(button => (button.Id, button.Visible)));
        Assert.All(once.Zip(twice), pair => Assert.NotSame(pair.First, pair.Second));
    }

    [Fact]
    public void Grouping_never_reorders_the_visible_individual_commands()
    {
        var buttons = ComposeToolbarCatalog.DefaultButtons();
        buttons.Reverse();
        buttons.Single(button => button.Id == "image").Visible = false;
        buttons.Single(button => button.Id == "italic").Visible = false;
        var groups = ComposeToolbarCatalog.GroupVisible(buttons);
        Assert.Equal(buttons.Where(button => button.Visible).Select(button => button.Id), groups.SelectMany(group => group.ItemIds));
        Assert.DoesNotContain("image", groups.SelectMany(group => group.ItemIds));
        Assert.DoesNotContain("italic", groups.SelectMany(group => group.ItemIds));
        Assert.All(groups, group => Assert.NotEmpty(group.ItemIds));
    }

    [Fact]
    public void Required_commands_are_standalone_always_visible_groups()
    {
        var buttons = ComposeToolbarCatalog.DefaultButtons();
        foreach (var button in buttons) button.Visible = false;
        var groups = ComposeToolbarCatalog.GroupVisible(buttons);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.True(group.AlwaysVisible));
        Assert.Equal("attach", Assert.Single(groups[0].ItemIds));
        Assert.Equal("more", Assert.Single(groups[1].ItemIds));
        Assert.Equal("Files", groups[0].Name);
        Assert.Equal("More", groups[1].Name);

        var defaults = ComposeToolbarCatalog.GroupVisible(null);
        Assert.True(defaults[0].AlwaysVisible);
        Assert.Equal("attach", Assert.Single(defaults[0].ItemIds));
        Assert.False(defaults[1].AlwaysVisible);
        Assert.Equal("recentFiles", Assert.Single(defaults[1].ItemIds));
    }

    [Fact]
    public void Only_consecutive_same_category_entries_are_grouped_together()
    {
        var order = new[] { "bold", "italic", "fontFamily", "strikeThrough", "underline" };
        var buttons = order.Concat(ExpectedIds.Except(order)).Select(id => new ToolbarButton
        {
            Id = id, Visible = order.Contains(id),
        });
        var groups = ComposeToolbarCatalog.GroupVisible(buttons);
        Assert.Equal(new[] { "Text styles", "Font", "Text styles", "Files", "More" }, groups.Select(group => group.Name));
        Assert.Equal(new[] { "bold", "italic" }, groups[0].ItemIds);
        Assert.Equal(new[] { "fontFamily" }, groups[1].ItemIds);
        Assert.Equal(new[] { "strikeThrough", "underline" }, groups[2].ItemIds);
    }

    [Fact]
    public void Hiding_an_intervening_group_leaves_adjacent_visible_peers_together()
    {
        var order = new[] { "bold", "fontFamily", "italic" };
        var buttons = order.Concat(ExpectedIds.Except(order)).Select(id => new ToolbarButton
        {
            Id = id, Visible = id is "bold" or "italic",
        });
        var groups = ComposeToolbarCatalog.GroupVisible(buttons);
        Assert.Equal(new[] { "bold", "italic" }, groups[0].ItemIds);
        Assert.Equal("Text styles", groups[0].Name);
        Assert.Equal(3, groups.Count); // Text styles, required Attach, required More.
    }

    [Fact]
    public void More_and_attach_remain_boundaries_even_between_same_category_entries()
    {
        var order = new[] { "bold", "more", "italic", "attach", "underline" };
        var buttons = order.Concat(ExpectedIds.Except(order)).Select(id => new ToolbarButton { Id = id, Visible = order.Contains(id) });
        var groups = ComposeToolbarCatalog.GroupVisible(buttons);
        Assert.Equal(order, groups.SelectMany(group => group.ItemIds));
        Assert.Equal(5, groups.Count);
        Assert.All(groups, group => Assert.Single(group.ItemIds));
        Assert.True(groups[1].AlwaysVisible);
        Assert.True(groups[3].AlwaysVisible);
    }

    [Fact]
    public void Toolbar_order_and_visibility_survive_settings_json_round_trip()
    {
        var settings = new AppSettings();
        settings.Appearance.ComposeToolbar.Reverse();
        settings.Appearance.ComposeToolbar.Single(button => button.Id == "fontSize").Visible = false;
        var json = JsonSerializer.Serialize(settings, AppSettings.Json);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Json)!;
        restored.Appearance.Normalise();
        Assert.Equal(settings.Appearance.ComposeToolbar.Select(button => (button.Id, button.Visible)),
            restored.Appearance.ComposeToolbar.Select(button => (button.Id, button.Visible)));
    }

    [Fact]
    public void Older_settings_and_null_toolbar_restore_defaults()
    {
        var older = JsonSerializer.Deserialize<Appearance>("""{"Theme":"Dark"}""", AppSettings.Json)!;
        Assert.Equal(ExpectedIds, older.ComposeToolbar.Select(button => button.Id));
        var missing = JsonSerializer.Deserialize<Appearance>("""{"ComposeToolbar":null}""", AppSettings.Json)!;
        missing.Normalise();
        Assert.Equal(ExpectedIds, missing.ComposeToolbar.Select(button => button.Id));
        Assert.All(missing.ComposeToolbar, button => Assert.True(button.Visible));
    }

    [Fact]
    public void Clone_is_deep_and_reset_does_not_change_the_original_or_more_menu()
    {
        var appearance = new Appearance();
        appearance.ComposeToolbar.Reverse();
        appearance.ComposeToolbar.Single(button => button.Id == "emoji").Visible = false;
        appearance.ComposeMoreOrder.Reverse();
        var originalOrder = appearance.ComposeToolbar.Select(button => button.Id).ToArray();
        var moreOrder = appearance.ComposeMoreOrder.ToArray();
        var clone = appearance.Clone();
        clone.ComposeToolbar[0].Visible = !clone.ComposeToolbar[0].Visible;
        clone.ComposeToolbar.Reverse();
        Assert.Equal(originalOrder, appearance.ComposeToolbar.Select(button => button.Id));
        Assert.NotEqual(appearance.ComposeToolbar[0].Visible, clone.ComposeToolbar[^1].Visible);
        clone.ComposeToolbar = ComposeToolbarCatalog.DefaultButtons();
        Assert.Equal(ExpectedIds, clone.ComposeToolbar.Select(button => button.Id));
        Assert.False(appearance.ComposeToolbar.Single(button => button.Id == "emoji").Visible);
        Assert.Equal(moreOrder, clone.ComposeMoreOrder);
        clone.ComposeMoreOrder.Clear();
        Assert.Equal(moreOrder, appearance.ComposeMoreOrder);
    }

    [Fact]
    public void Compose_normalization_does_not_change_reading_toolbar_or_more_menu_order()
    {
        var appearance = new Appearance();
        appearance.Toolbar.Reverse();
        appearance.ComposeMoreOrder.Reverse();
        appearance.ComposeToolbar = new() { new() { Id = "emoji", Visible = false } };
        var reading = appearance.Toolbar.Select(button => (button.Id, button.Visible)).ToArray();
        var more = appearance.ComposeMoreOrder.ToArray();
        appearance.Normalise();
        Assert.Equal(reading, appearance.Toolbar.Select(button => (button.Id, button.Visible)));
        Assert.Equal(more, appearance.ComposeMoreOrder);
        Assert.Equal("emoji", appearance.ComposeToolbar[0].Id);
        Assert.False(appearance.ComposeToolbar[0].Visible);
    }
}
