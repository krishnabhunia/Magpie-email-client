using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Magpie.App.Services;
using Magpie.App.Views;
using Magpie.Core.Mail;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckEmojiPickerAsync(string root, Func<string, Task<string>> script)
    {
        var directory = Path.Combine(root, "emoji-history");
        var history = new ComposeEmojiHistory(directory);
        Assert(history.Read().Count == 0, "Missing local emoji history is empty");
        history.Remember("🙂"); history.Remember("🙏"); history.Remember("🙂"); history.Remember("unknown");
        Assert(new ComposeEmojiHistory(directory).Read().SequenceEqual(new[] { "🙂", "🙏" }),
            "Recent emoji survive reopening, deduplicate and keep most-recent first");
        var filename = Path.Combine(directory, "recent-compose-emoji.json");
        File.WriteAllText(filename, "{broken");
        Assert(history.Read().Count == 0, "Corrupted emoji history cannot break the picker");
        File.WriteAllText(filename, new string('x', 8193));
        Assert(history.Read().Count == 0, "Oversized local emoji history is not loaded");
        history.Remember("🙏");
        Assert(history.Read().SequenceEqual(new[] { "🙏" }), "The next emoji insertion safely repairs invalid recent history");

        string? inserted = null;
        var menu = ComposeEmojiPicker.Create(new[] { "₹", "🙏" }, value => { inserted = value; return Task.FromResult(true); });
        var panel = (StackPanel)((MenuItem)menu.Items[0]).Header;
        var search = panel.Children.OfType<TextBox>().Single();
        var category = panel.Children.OfType<ComboBox>().Single();
        var grid = (UniformGrid)panel.Children.OfType<ScrollViewer>().Single().Content;
        Assert(grid.Children.Count == ComposeEmojiCatalog.Entries.Count
            && grid.Children.OfType<Button>().All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))),
            "Native emoji picker renders its offline catalog with accessible names");
        search.Text = "namaste";
        Assert(grid.Children.Count == 1 && ((Button)grid.Children[0]).Tag?.ToString() == "🙏", "Native emoji search matches useful keywords");
        ((Button)grid.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(inserted == "🙏", "Choosing an emoji sends its exact text value to the insertion callback");
        search.Text = ""; category.SelectedItem = "Recent";
        Assert(grid.Children.OfType<Button>().Select(button => button.Tag?.ToString()).SequenceEqual(new[] { "₹", "🙏" }),
            "Native Recent category follows local history order");
        category.SelectedItem = "Work";
        Assert(grid.Children.Count > 0 && grid.Children.OfType<Button>().All(button => ComposeEmojiCatalog.Search("", "Work").Any(entry => entry.Value == button.Tag?.ToString())),
            "Native category picker restricts the shown choices");
        search.Text = "no-such-emoji";
        Assert(grid.Children.Count == 0 && panel.Children.OfType<TextBlock>().Last().Text.StartsWith("No matches"),
            "An empty search displays guidance rather than an empty unexplained panel");

        await script("setHtml('<p><strong>Pick me</strong> and preserve <em>emphasis</em></p>');findInEditor('Pick',true)");
        Assert(await script("captureEmojiSelection()") == "true", "Emoji picker captures the selected rich text");
        await script("window.getSelection().removeAllRanges()");
        Assert(await script("insertSelectedEmoji('🙏')") == "true"
            && JsonSerializer.Deserialize<string>(await script("ed.textContent")) == "🙏 me and preserve emphasis"
            && JsonSerializer.Deserialize<string>(await script("ed.querySelector('strong')?.textContent")) == "🙏 me"
            && await script("ed.querySelector('em')?.textContent") == "\"emphasis\"",
            "Emoji insertion restores the selected range and preserves surrounding rich formatting");
        await script("captureEmojiSelection();ed.appendChild(document.createTextNode(' changed'))");
        Assert(await script("insertSelectedEmoji('🙂')") == "false", "Emoji insertion refuses a stale edited draft snapshot");
        await script("setHtml('<p>Plain message</p>');setEditorPlainText(true);findInEditor('message',true);captureEmojiSelection()");
        Assert(await script("insertSelectedEmoji('₹')") == "true"
            && JsonSerializer.Deserialize<string>(await script("ed.textContent")) == "Plain ₹"
            && await script("ed.contentEditable") == "\"plaintext-only\"", "Emoji also insert as plain Unicode text in plain-text mode");
        await script("captureEmojiSelection();cancelEmojiSelection()");
        Assert(await script("insertSelectedEmoji('🙂')") == "false", "Closing the emoji picker invalidates its saved insertion target");
        await script("setHtml('<p>Restored</p>')");
        Assert(await script("insertSelectedEmoji('🙂')") == "false", "Draft restoration invalidates pending emoji insertion");
        Console.WriteLine("EMOJI_PICKER_SMOKE_PASSED");
    }
}
