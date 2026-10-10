using Avalonia.Input;
using Magpie.Mac.Services;
using Xunit;

namespace Magpie.Mac.Tests;

/// <summary>⌘ keys pressed inside Magpie's own web pages reach the menu commands (found on a real Mac: ⌘W did nothing
/// in the compose editor).</summary>
public class MenuKeyTests
{
    [Fact]
    public void Keys_map_to_menu_gestures()
    {
        Assert.Equal(Key.W, MacMenus.KeyFor("w"));
        Assert.Equal(Key.D1, MacMenus.KeyFor("1"));
        Assert.Equal(Key.OemComma, MacMenus.KeyFor(","));
        Assert.Null(MacMenus.KeyFor("enter"));
        Assert.Null(MacMenus.KeyFor("é"));
    }

    [Fact]
    public void The_forwarding_script_goes_before_the_end_of_the_page_and_leaves_editing_keys_alone()
    {
        var page = MacMenus.WithKeyForwarding("<html><body><p>x</p></body></html>");
        Assert.Contains("menukey", page);
        Assert.EndsWith("</body></html>", page);
        Assert.Contains("own = {c:1, v:1, x:1, a:1, z:1, b:1, i:1, u:1, k:1}", MacMenus.KeyForwardScript);
        Assert.Contains("menukey", MacMenus.WithKeyForwarding("<p>no body tag</p>"));
    }
}
