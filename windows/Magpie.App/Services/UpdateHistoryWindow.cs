using System.Windows;
using System.Windows.Controls;
using Magpie.Core;
using Magpie.Core.Updates;

namespace Magpie.App.Services;

/// <summary>Local transition records and their paired application/data recovery points.</summary>
public sealed class UpdateHistoryWindow : Window
{
    public string? SnapshotId { get; private set; }

    public UpdateHistoryWindow(AppPaths paths)
    {
        Title = "Magpie · Update and rollback history";
        Width = 760; Height = 560; MinWidth = 620; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        var root = new DockPanel { Margin = new Thickness(22) };
        var header = new TextBlock { Text = "Application and data recovery", FontSize = 21, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
        var restore = new Button { Content = "Choose a recovery point", IsEnabled = false };
        restore.SetResourceReference(StyleProperty, "Button.Primary");
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
        close.SetResourceReference(StyleProperty, "Button.Secondary");
        close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(restore); actions.Children.Add(close);
        var footer = new StackPanel(); footer.Children.Add(detail); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var list = new ListBox { DisplayMemberPath = nameof(HistoryRow.Label) };
        root.Children.Add(list); Content = root;
        var entries = UpdateHistory.List(paths).Select(e => new HistoryRow(e)).ToList();
        list.ItemsSource = entries; list.SelectedIndex = -1;
        detail.Text = entries.Count == 0 ? "No version changes have been recorded on this device."
            : "Select an update or rollback to inspect its result and saved recovery point.";
        list.SelectionChanged += (_, _) =>
        {
            SnapshotId = null; restore.IsEnabled = false;
            if (list.SelectedItem is not HistoryRow row) return;
            detail.Text = row.Entry.Detail;
            if (row.Entry.SnapshotId is not { } id) return;
            try
            {
                var snapshot = VersionBackup.Read(paths, id);
                detail.Text += $"\n\nRecovery point: Magpie v{snapshot.Version}, {snapshot.Created.LocalDateTime:g}. " +
                    "Restores the saved application, local mail, drafts, settings and encrypted sign-ins together.";
                restore.Content = $"Restore v{snapshot.Version} and saved data";
                restore.IsEnabled = snapshot.InstallKind == UpdateService.InstallKind;
                if (!restore.IsEnabled) detail.Text += "\nThis recovery point belongs to a different installation type.";
            }
            catch (Exception ex) { detail.Text += "\n\nThe recovery point cannot be used: " + ex.Message; }
        };
        restore.Click += (_, _) =>
        {
            if (!restore.IsEnabled || list.SelectedItem is not HistoryRow row || row.Entry.SnapshotId == null) return;
            SnapshotId = row.Entry.SnapshotId; DialogResult = true;
        };
    }

    private sealed record HistoryRow(UpdateHistoryEntry Entry)
    {
        public string Label => $"{Entry.Created.LocalDateTime:g}  ·  {Entry.Action}  ·  v{Entry.From} → v{Entry.To}\n{Entry.Status}";
    }
}
