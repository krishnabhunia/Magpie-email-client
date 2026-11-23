using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;

namespace Magpie.App.Views;

/// <summary>
/// Design OW1: the head of a reader — toolbar, subject, AI summary, meeting invite and the banners — as the main
/// window's reading pane and an email's own window both show it. The view only draws; its host runs the actions.
/// </summary>
public partial class ReaderHeadView : UserControl
{
    private IReaderHost? _host;
    private readonly HoverCard _deleteCard;

    public ReaderHeadView()
    {
        InitializeComponent();
        // Design DD1 (R2): the chip next to the subject opens its card on hover, a click, or Enter / Space / Menu.
        _deleteCard = new HoverCard(DeleteCardFor);
        _deleteCard.Attach(DeleteChip);
        DeleteChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; _deleteCard.Show(DeleteChip); };
        DeleteChip.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space or Key.Apps) { e.Handled = true; _deleteCard.Show(DeleteChip, focus: true); } };
    }

    /// <summary>
    /// Gives the view its host. The toolbar shows the buttons chosen in Settings → Toolbar &amp; buttons; an email's own
    /// window leaves out "New window" (it is that window).
    /// </summary>
    public void Attach(IReaderHost host)
    {
        _host = host;
        ToolbarItems.ItemsSource = host.OwnWindow
            ? new ListCollectionView(host.Main.ToolbarButtons) { Filter = b => b is ToolbarButtonVm { Id: not "window" } }
            : host.Main.ToolbarButtons;
    }

    /// <summary>How many buttons the toolbar shows now (the smoke test reads this).</summary>
    public int ToolbarCount => ToolbarItems.Items.Count;

    /// <summary>Closes the card of the delete date (the window moved or lost the focus).</summary>
    public void CloseCards() => _deleteCard.Close();

    private UIElement? DeleteCardFor(FrameworkElement target)
    {
        if (_host is not { } host) return null;
        var r = host.Reader;
        if (!r.HasDeleteTimer || !r.HasThread) return null;
        return ReaderActions.DeleteCard(host.Window, _deleteCard.Close, r.DeleteCardTitle, r.DeleteRuleText, r.DeleteRuleId,
            () => r.KeepFromAutoDeleteCommand.Execute(null), deleteNow: null);
    }

    /// <summary>A toolbar button (designs C1, C3).</summary>
    private void OnToolbarButton(object sender, RoutedEventArgs e)
    {
        if (_host != null && sender is FrameworkElement { Tag: ToolbarButtonVm b } anchor) _host.RunAction(b.Id, anchor);
    }

    private void OnDeleteDropdown(object sender, RoutedEventArgs e)
    {
        if (_host is not { Reader.HasThread: true } host || sender is not FrameworkElement anchor) return;
        ReaderActions.ShowMenu(anchor, ReaderActions.DeleteMenu(host, withDeleteNow: true));
    }

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        if (_host is not { Reader.HasThread: true } host || sender is not FrameworkElement anchor) return;
        ReaderActions.ShowMenu(anchor, ReaderActions.MoreMenu(host, anchor));
    }

    /// <summary>Design AL1: Restore on the "Deleted by Magpie auto delete" banner — the conversation goes back where it was.</summary>
    private void OnRestoreAutoDeleted(object sender, RoutedEventArgs e) => _host?.RestoreFromTrash();

    /// <summary>Design AL1: Edit rule on that banner — the rule that deleted the email, while it still exists.</summary>
    private void OnEditAutoDeletedRule(object sender, RoutedEventArgs e)
    {
        if (_host is { } host) ReaderActions.EditRule(host.Window, host.Reader.AutoDeletedRuleId, ReaderActions.RuleGoneAfterDelete);
    }

    /// <summary>"Change rule" on the delete-date banner (design AD3).</summary>
    private void OnChangeDeleteRule(object sender, RoutedEventArgs e)
    {
        if (_host is { } host) ReaderActions.EditRule(host.Window, host.Reader.DeleteRuleId, ReaderActions.RuleGoneKeepsDate);
    }
}
