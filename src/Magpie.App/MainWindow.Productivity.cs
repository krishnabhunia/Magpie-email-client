using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core.Ai;
using Magpie.Core.Models;

namespace Magpie.App;

public partial class MainWindow
{
    private void OnSearchFilter(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string filter }) return;
        var parsed = Magpie.Core.Storage.SearchQuery.Parse(_vm.SearchText);
        if (filter == "has:attachment" && parsed.HasAttachment || filter == "is:pinned" && parsed.Pinned) return;
        _vm.SearchText = (_vm.SearchText.Trim() + " " + filter).Trim();
    }

    private void OnSaveSearch(object sender, RoutedEventArgs e) => EditWorkspaceView(null, _vm.SearchText);

    private void OnCommands(object sender, RoutedEventArgs e) => OpenCommands();

    private void OpenCommands()
    {
        var engine = AppServices.Engine;
        bool MailSelected() => !_vm.IsCalendarView && _vm.Reader.HasThread && _vm.Selected?.LocalDraftId == null && _vm.SelectedCount == 0;
        bool CanCompose() => engine.Accounts.Any(a => a.Enabled);
        const string selectedReason = "Open a conversation in Mail and clear any bulk selection first.";
        var commands = new List<WorkspaceCommand>();
        void Add(string title, string detail, string key, string aliases, Action run, Func<bool>? available = null, string reason = "")
            => commands.Add(new WorkspaceCommand(title, detail, key, aliases, run, available, reason));

        foreach (var nav in _vm.Smart.Concat(_vm.AccountNodes.SelectMany(n => n.Folders)).Concat(_vm.TagItems))
        {
            var item = nav;
            var detail = item.SavedView is { } saved
                ? (saved.InboxOnly ? "Local inbox view: " : "Saved local search: ") + saved.Query
                : item.AccountId is { } account ? engine.AccountById(account)?.Email + " · " + item.Path : "Open " + item.Label;
            Add("Go to " + item.Label, detail ?? "", "", "navigate folder inbox view " + item.Path, () => _vm.OpenWorkspaceView(item));
        }
        Add("Search mail", "Search headers and downloaded bodies on this PC.", "Ctrl+F", "find filter", () =>
        { _vm.IsCalendarView = false; SearchBox.Focus(); SearchBox.SelectAll(); });
        Add("Find unread mail", "Search all mail folders on this PC.", "", "search filter chip", () => _vm.SearchEverywhere("is:unread"));
        Add("Find attachments", "Search mail with attachments on this PC.", "", "search filter chip files", () => _vm.SearchEverywhere("has:attachment"));
        Add("Find pinned mail", "Search pinned mail on this PC.", "", "search important filter chip", () => _vm.SearchEverywhere("is:pinned"));
        Add("Find this sender", "Search locally across mail folders for the sender of the open message.", "", "from search",
            () => _vm.SearchEverywhere("from:\"" + _vm.Reader.SenderAddress.Replace("\"", "") + "\""), MailSelected, selectedReason);
        Add("Clear search", "Remove the current query.", "Esc in search", "reset filter", () => _vm.SearchText = "");
        Add("New inbox view", "Save a sender, domain, recipient, subject or label query on this PC.", "", "split inbox custom organize",
            () => EditWorkspaceView(null, ""));
        Add("Save current search", "Save this query as a local inbox view or search across mail folders.", "", "saved split query",
            () => EditWorkspaceView(null, _vm.SearchText), () => !string.IsNullOrWhiteSpace(_vm.SearchText), "Enter a search query first.");
        var currentView = _vm.Current?.SavedView;
        Add("Edit current saved view", "Change the name, query or account scope.", "", "split modify",
            () => EditWorkspaceView(currentView), () => currentView != null, "Open a saved view first.");
        Add("Remove current saved view", "Remove only the saved view; emails stay where they are.", "", "split remove",
            () =>
            {
                if (currentView != null && MessageBox.Show(this, "Remove saved view \"" + currentView.Name + "\"?",
                    "Remove saved view", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _vm.RemoveWorkspaceView(currentView.Id);
            }, () => currentView != null, "Open a saved view first.");
        Add("Compose email", "Open a new draft. Magpie saves edits on this PC.", "Ctrl+N", "new write drafting", () => _vm.ComposeCommand.Execute(null), CanCompose, "Enable an email account first.");
        Add("Draft with AI", "Open the AI draft panel. Review the preview and choose Insert; sending stays separate.", "", "write assistant prompt",
            ComposeWindow.OpenAiDraft, () => CanCompose() && engine.Ai.IsVisible(AiFeature.Draft),
            "Enable an account and turn on Draft in Settings → AI.");
        Add("AI settings", "Choose an AI provider and enable drafting or rewriting.", "", "assistant local ollama privacy", () => SettingsWindow.Open("AI"));
        Add("Reply", "Open a reply draft.", "R", "respond write", () => _vm.Reader.ReplyCommand.Execute(null), MailSelected, selectedReason);
        Add("Reply all", "Open a reply to everyone.", "Shift+R", "respond recipients", () => _vm.Reader.ReplyAllCommand.Execute(null), MailSelected, selectedReason);
        Add("Forward", "Open a forwarding draft.", "F", "share email", () => _vm.Reader.ForwardCommand.Execute(null), MailSelected, selectedReason);
        Add("Remind me", "Choose a time. If your latest message is outgoing, remind only if nobody replies.", "", "follow up waiting deadline",
            () => OnRemindMenu(ThreadList, new RoutedEventArgs()), MailSelected, selectedReason);
        Add("Snooze", "Choose when this conversation returns to the inbox.", "S", "later remind",
            () => OnSnoozeMenu(ThreadList, new RoutedEventArgs()), MailSelected, selectedReason);
        Add("Archive conversation", "Use Magpie's existing archive action.", "E", "done inbox",
            () => _vm.Reader.ArchiveCommand.Execute(null), MailSelected, selectedReason);
        Add("Move conversation to Trash", "Use Magpie's existing delete action and Undo.", "Delete", "delete",
            () => _vm.Reader.DeleteCommand.Execute(null), MailSelected, selectedReason);
        Add("Toggle pin", "Pinned inbox conversations appear in Important.", "P", "priority star important",
            () => _vm.Reader.TogglePinCommand.Execute(null), MailSelected, selectedReason);
        Add("Next conversation", "Open the next row using the prepared local reader.", "J", "navigate down",
            () => _vm.MoveSelection(1), () => !_vm.IsCalendarView && _vm.Threads.Count > 0, "Open a mail list first.");
        Add("Previous conversation", "Open the previous row using the prepared local reader.", "K", "navigate up",
            () => _vm.MoveSelection(-1), () => !_vm.IsCalendarView && _vm.Threads.Count > 0, "Open a mail list first.");
        Add("Open calendar", "Switch to the calendar workspace.", "Ctrl+2", "navigate", () => _vm.IsCalendarView = true);
        Add("Return to mail", "Switch to the mail workspace.", "Ctrl+1", "navigate", () => _vm.IsCalendarView = false);
        Add("Check for new mail", "Sync accounts; opening cached mail stays local.", "F5", "refresh update sync", () => _vm.SyncAllCommand.Execute(null));
        Add("Settings", "Open Magpie settings.", "", "preferences", () => SettingsWindow.Open(null));

        var palette = new CommandPaletteWindow(this, commands);
        if (palette.ShowDialog() == true && palette.Picked is { Enabled: true } command)
        {
            try { command.Run(); }
            catch (Exception ex) { Ui.Error(command.Title, ex.Message); }
        }
    }

    private void EditWorkspaceView(InboxView? view, string query = "")
    {
        var dialog = new InboxViewDialog(this, query, view)
        {
            Save = (name, text, account, inbox) => _vm.SaveWorkspaceView(name, text, account, inbox, view?.Id)
        };
        dialog.ShowDialog();
    }

    // Letter shortcuts must not archive/delete while typing in a WPF editor or WebView.
    private bool IsMailEditing()
        => Keyboard.FocusedElement is TextBoxBase or PasswordBox || Web.IsKeyboardFocusWithin
            || Keyboard.FocusedElement is System.Windows.Controls.ComboBox { IsEditable: true };
}
