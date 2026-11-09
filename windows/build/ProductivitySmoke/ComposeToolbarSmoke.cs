using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>Mount only the real toolbar, so native overflow is exercised without starting the email editor.</summary>
    private static void CheckComposeToolbar(Window owner, MailEngine engine, string theme)
    {
        var saved = engine.Config.Appearance.ComposeToolbar;
        var moreOrder = engine.Config.Appearance.ComposeMoreOrder.ToArray();
        var window = new ComposeWindow(new Draft { AccountId = "A", Html = "<p>Toolbar fixture</p>" }, null);
        var toolbar = (ToolBar)window.FindName("FormattingItems");
        var originalHost = (Border)window.FindName("FormattingToolbar");
        var controls = (Dictionary<string, FrameworkElement>)typeof(ComposeWindow)
            .GetField("_composeToolbarControls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var font = (ComboBox)controls["fontFamily"];
        originalHost.Child = null;
        var probe = new Window
        {
            Content = toolbar, Width = 700, Height = 170, Left = -3000, Top = -3000,
            Owner = owner, ShowActivated = false, ShowInTaskbar = false,
        };
        toolbar.HorizontalAlignment = HorizontalAlignment.Left;
        toolbar.VerticalAlignment = VerticalAlignment.Top;
        try
        {
            Assert(controls.Count == ComposeToolbarCatalog.Definitions.Count
                && controls.Keys.OrderBy(id => id).SequenceEqual(ComposeToolbarCatalog.Definitions.Select(item => item.Id).OrderBy(id => id)),
                theme + ": every main-toolbar setting maps to the actual XAML command control");
            font.SelectedIndex = 2;
            toolbar.Width = 620;
            probe.Show();
            probe.UpdateLayout();
            PumpUntil(() => toolbar.HasOverflowItems);
            var groups = toolbar.Items.Cast<Border>().ToArray();
            Assert(groups.Where(group => ((ComposeToolbarGroup)group.Tag).AlwaysVisible).All(group => !ToolBar.GetIsOverflowItem(group))
                && groups.Any(group => ToolBar.GetIsOverflowItem(group)),
                theme + ": narrow windows overflow whole groups while Attach and More stay on the main row");
            var toggle = (ToggleButton)toolbar.Template.FindName("OverflowButton", toolbar);
            Assert(toggle.Visibility == Visibility.Visible && toggle.Content?.ToString() == "More tools ▾"
                && AutomationProperties.GetName(toggle) == "More formatting tools",
                theme + ": responsive overflow has a visible and accessible More tools affordance");
            var popup = (Popup)toolbar.Template.FindName("PART_Popup", toolbar);
            toggle.IsChecked = true;
            PumpUntil(() => toolbar.IsOverflowOpen && popup.IsOpen);
            probe.UpdateLayout();
            var overflow = (ToolBarOverflowPanel)toolbar.Template.FindName("PART_ToolBarOverflowPanel", toolbar);
            Assert(overflow.Children.Count > 0 && overflow.Children.Cast<Border>().All(group =>
                    ((StackPanel)group.Child).Children.OfType<FrameworkElement>().All(control =>
                        ReferenceEquals(control, controls[ComposeToolbarHost.GetId(control)]))),
                theme + ": native overflow opens the original controls, keeping their handlers and selected values");
            toolbar.IsOverflowOpen = false;
            PumpUntil(() => !popup.IsOpen);

            toolbar.Width = 3000;
            probe.UpdateLayout();
            PumpUntil(() => !toolbar.HasOverflowItems);
            Assert(toggle.Visibility == Visibility.Collapsed && groups.All(group => !ToolBar.GetIsOverflowItem(group)),
                theme + ": a wide toolbar restores every group and hides the unused overflow affordance");

            var layout = ComposeToolbarCatalog.DefaultButtons();
            layout.Single(button => button.Id == "bold").Visible = false;
            layout.Single(button => button.Id == "attach").Visible = false; // Corrupt/old settings may not hide the access anchor.
            var emoji = layout.Single(button => button.Id == "emoji");
            layout.Remove(emoji); layout.Insert(0, emoji);
            engine.Config.Appearance.ComposeToolbar = layout;
            RecipientInvoke(window, "ApplyComposeToolbarLayout");
            probe.UpdateLayout();
            string[] VisibleIds() => toolbar.Items.Cast<Border>().SelectMany(group => ((ComposeToolbarGroup)group.Tag).ItemIds).ToArray();
            var visible = VisibleIds();
            Assert(visible[0] == "emoji" && !visible.Contains("bold") && visible.Contains("attach") && visible.Contains("more"),
                theme + ": the saved individual order and visibility apply without hiding required controls");
            Assert(font.SelectedIndex == 2 && ReferenceEquals(window.FindName("FontFamilyChoice"), font)
                && toolbar.Items.Cast<Border>().SelectMany(group => ((StackPanel)group.Child).Children.OfType<FrameworkElement>())
                    .All(control => ReferenceEquals(control, controls[ComposeToolbarHost.GetId(control)])),
                theme + ": applying layout preserves original combo values, named controls and event wiring");
            var firstGroup = toolbar.Items[0];
            RecipientInvoke(window, "ApplyComposeToolbarLayout");
            Assert(ReferenceEquals(firstGroup, toolbar.Items[0]),
                theme + ": unrelated settings updates do not reparent controls or disturb keyboard focus");
            var vm = (ComposeViewModel)window.DataContext;
            vm.IsPlainText = true;
            RecipientInvoke(window, "UpdatePlainTextControls");
            Assert(!((Control)controls["bold"]).IsEnabled && !((Control)controls["fontFamily"]).IsEnabled
                && ((Control)controls["emoji"]).IsEnabled && ((Control)controls["more"]).IsEnabled
                && ((Control)controls["attach"]).IsEnabled,
                theme + ": regrouping and hiding do not bypass plain-text formatting guards");
            engine.Config.Appearance.ComposeToolbar = ComposeToolbarCatalog.DefaultButtons();
            RecipientInvoke(window, "ApplyComposeToolbarLayout");
            Assert(VisibleIds().SequenceEqual(ComposeToolbarCatalog.Definitions.Select(item => item.Id))
                && engine.Config.Appearance.ComposeMoreOrder.SequenceEqual(moreOrder),
                theme + ": reset restores all main commands without changing the separate More command order");
        }
        finally
        {
            toolbar.IsOverflowOpen = false;
            probe.Content = null;
            probe.Close();
            originalHost.Child = toolbar;
            engine.Config.Appearance.ComposeToolbar = saved;
            typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            ((ComposeViewModel)window.DataContext).Detach();
        }
    }
}
