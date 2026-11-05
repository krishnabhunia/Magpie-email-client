using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Magpie.Core.Updates;

namespace Magpie.App.Services;

/// <summary>
/// The version picker's confirm screen (design VP1, Q90): From → To, the amber note (beta skipping a newer stable, or
/// a roll back), and the numbered steps that will run. Nothing starts until the action button is pressed.
/// </summary>
public sealed class VersionConfirmWindow : Window
{
    /// <summary>True when the user chose "Pick a different version" (the caller opens the list again).</summary>
    public bool PickAgain { get; private set; }

    public VersionConfirmWindow(AppVersion current, VersionChoice target, IReadOnlyList<VersionChoice> choices, bool onThisPc)
    {
        var to = target.Release.Version;
        var rollback = to.CompareTo(current) < 0;
        Title = rollback ? "Magpie · Confirm roll back" : "Magpie · Confirm update";
        Width = 760; SizeToContent = SizeToContent.Height; MinWidth = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        NameScope.SetNameScope(this, new NameScope());

        var root = new StackPanel { Margin = new Thickness(24) };
        Content = root;
        root.Children.Add(new TextBlock { Text = rollback ? "Confirm roll back" : "Confirm update", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });

        // From → To
        var cards = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        var from = Card("FROM (INSTALLED)", $"v{current}", current.IsPrerelease, "A recovery copy is saved before anything changes.", accent: false);
        var arrow = new TextBlock { Text = "→", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        System.Windows.Automation.AutomationProperties.SetName(arrow, "changes to");
        var toDetail = target.Release.Published is { } p ? $"Released {p.ToLocalTime():d MMM yyyy}" : "Release date not published";
        if (target.Release.ExeSize > 0) toDetail += $" · {target.Release.ExeSize / 1048576.0:0} MB";
        if (onThisPc) toDetail += " · On this PC";
        var toCard = Card("TO (YOUR CHOICE)", $"v{to}", target.Prerelease, toDetail, accent: true);
        Grid.SetColumn(arrow, 1); Grid.SetColumn(toCard, 2);
        cards.Children.Add(from); cards.Children.Add(arrow); cards.Children.Add(toCard);
        root.Children.Add(cards);

        // Amber note
        if (VersionPicker.Warning(target, choices, current) is { } warning)
        {
            var note = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14) };
            note.SetResourceReference(Border.BackgroundProperty, "Brush.Warning.Subtle");
            var text = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap };
            RegisterName("ConfirmWarning", text);
            note.Child = text;
            root.Children.Add(note);
        }

        // What will happen
        var steps = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 16) };
        steps.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");
        steps.SetResourceReference(Border.BorderBrushProperty, "Brush.Card.Border");
        var list = new StackPanel();
        list.Children.Add(new TextBlock { Text = "What will happen", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var items = VersionPicker.Steps(current, target, onThisPc);
        var stepsList = new ItemsControl { ItemsSource = items.Select((s, i) => $"{i + 1}.  {s}").ToList() };
        stepsList.ItemTemplate = StepTemplate();
        RegisterName("ConfirmSteps", stepsList);
        list.Children.Add(stepsList);
        steps.Child = list;
        root.Children.Add(steps);

        // Buttons
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var back = new Button { Content = "Pick a different version", MinWidth = 170 };
        back.SetResourceReference(StyleProperty, "Button.Secondary");
        back.Click += (_, _) => { PickAgain = true; DialogResult = false; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(10, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "Button.Secondary");
        var go = new Button { Content = VersionPicker.ActionText(current, to), MinWidth = 190, Margin = new Thickness(10, 0, 0, 0) };
        go.SetResourceReference(StyleProperty, "Button.Primary");
        go.Click += (_, _) => DialogResult = true;
        RegisterName("PickAgainButton", back);
        RegisterName("ConfirmButton", go);
        actions.Children.Add(back); actions.Children.Add(cancel); actions.Children.Add(go);
        root.Children.Add(actions);
    }

    private static Border Card(string eyebrow, string version, bool beta, string detail, bool accent)
    {
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 14), BorderThickness = new Thickness(accent ? 2 : 1) };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");
        card.SetResourceReference(Border.BorderBrushProperty, accent ? "Brush.Accent" : "Brush.Card.Border");
        var stack = new StackPanel();
        var head = new TextBlock { Text = eyebrow, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        head.SetResourceReference(TextBlock.ForegroundProperty, accent ? "Brush.Accent.Text" : "Brush.Text.Secondary");
        stack.Children.Add(head);
        var line = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        line.Children.Add(new TextBlock { Text = version, FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(Border.BackgroundProperty, beta ? "Brush.Warning.Subtle" : "Brush.Accent.Subtle");
        pill.Child = new TextBlock { Text = beta ? "Beta" : "Stable", FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        line.Children.Add(pill);
        stack.Children.Add(line);
        var sub = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        stack.Children.Add(sub);
        card.Child = stack;
        return card;
    }

    private static DataTemplate StepTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(MarginProperty, new Thickness(0, 3, 0, 3));
        return new DataTemplate { VisualTree = text };
    }
}
