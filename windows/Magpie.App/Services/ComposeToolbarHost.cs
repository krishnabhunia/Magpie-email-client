using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.Core.Settings;

namespace Magpie.App.Services;

/// <summary>
/// Reorders the existing command controls, keeping adjacent command groups together in native WPF overflow.
/// No handlers, editor selections or combo-box values are recreated when a layout is applied.
/// </summary>
public static class ComposeToolbarHost
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(ComposeToolbarHost), new PropertyMetadata(""));

    public static void SetId(DependencyObject element, string value) => element.SetValue(IdProperty, value);
    public static string GetId(DependencyObject element) => (string)element.GetValue(IdProperty);

    public static void Apply(ToolBar toolbar, IReadOnlyDictionary<string, FrameworkElement> controls, IEnumerable<ToolbarButton>? buttons)
    {
        toolbar.IsOverflowOpen = false;
        // The children must leave their old logical parent before they can enter the new ordered groups.
        foreach (var group in toolbar.Items.OfType<Border>())
            if (group.Child is StackPanel panel) panel.Children.Clear();
        toolbar.Items.Clear();
        foreach (var definition in ComposeToolbarCatalog.GroupVisible(buttons))
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetRole(panel, "editorToolbar");
            foreach (var id in definition.ItemIds)
            {
                if (!controls.TryGetValue(id, out var control)) continue;
                panel.Children.Add(control);
            }
            if (panel.Children.Count == 0) continue;
            var group = new Border
            {
                Child = panel, Tag = definition, Margin = new Thickness(0, 2, 5, 2),
                Padding = new Thickness(0, 0, 5, 0), BorderThickness = new Thickness(0, 0, 1, 0),
            };
            group.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
            AutomationProperties.SetName(group, definition.Name + " formatting tools");
            ToolBar.SetOverflowMode(group, definition.AlwaysVisible ? OverflowMode.Never : OverflowMode.AsNeeded);
            toolbar.Items.Add(group);
        }
    }
}
