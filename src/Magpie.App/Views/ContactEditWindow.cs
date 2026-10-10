using System.Windows;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.Core.Contacts;
using Magpie.Core.Models;

namespace Magpie.App.Views;

/// <summary>
/// Design B4: add or change a contact (also "Add to contacts" from Recent and the address card, E6). Saved on this PC at
/// once and sent to the Google account in the background.
/// </summary>
public static class ContactEditWindow
{
    public static void Edit(Window? owner, SavedContact contact)
    {
        var engine = AppServices.Engine;
        var accounts = engine.Contacts.GoogleAccounts;
        if (accounts.Count == 0)
        {
            Ui.Error("Contacts", "Sign in to a Google account (Settings → Accounts) so Magpie can save contacts to it.", owner);
            return;
        }
        var isNew = contact.Id == 0;
        var w = new Window { Title = (isNew ? "New contact" : "Edit contact") + " — Magpie", Width = 500, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
        w.Style = (Style)Application.Current.FindResource("Window.Dialog");
        if (owner is { IsVisible: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Style S(string key) => (Style)Application.Current.FindResource(key);
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        panel.Children.Add(new TextBlock { Text = isNew ? "New contact" : "Edit contact", Style = S("Text.Serif"), FontSize = 17, Margin = new Thickness(0, 0, 0, 12) });

        TextBox Field(string label, string value, bool multi = false, string hint = "")
        {
            panel.Children.Add(new TextBlock { Text = label, Style = S("Text.Caption"), Margin = new Thickness(0, 8, 0, 4) });
            var box = new TextBox { Text = value, Style = S("TextBox.Field"), AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                                    MinHeight = multi ? 54 : 0, VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            panel.Children.Add(box);
            if (hint.Length > 0) panel.Children.Add(new TextBlock { Text = hint, Style = S("Text.Caption"), Margin = new Thickness(0, 3, 0, 0) });
            return box;
        }

        // A contact from Google with only a display name: put it in the first-name box so it isn't lost.
        var given = contact.GivenName.Length == 0 && contact.FamilyName.Length == 0 ? contact.Name : contact.GivenName;
        var first = Field("First name", given);
        var last = Field("Last name", contact.FamilyName);
        var emails = Field("Email", string.Join("\n", contact.Emails), multi: true, hint: "One address per line; the first is used for new messages.");
        var phones = Field("Phone", string.Join("\n", contact.Phones), multi: true, hint: "One number per line.");
        var company = Field("Company", contact.Company);
        var title = Field("Job title", contact.Title);
        var notes = Field("Notes", contact.Notes, multi: true);

        ComboBox? where = null;
        if (isNew && accounts.Count > 1)
        {
            panel.Children.Add(new TextBlock { Text = "Save in", Style = S("Text.Caption"), Margin = new Thickness(0, 8, 0, 4) });
            where = new ComboBox { ItemsSource = accounts.Select(a => a.Email).ToList(), SelectedIndex = Math.Max(0, accounts.ToList().FindIndex(a => a.Id == contact.AccountId)) };
            panel.Children.Add(where);
        }
        else if (!isNew)
            panel.Children.Add(new TextBlock { Text = "Saved in " + (engine.AccountById(contact.AccountId)?.Email ?? "Google"), Style = S("Text.Caption"), Margin = new Thickness(0, 10, 0, 0) });

        var error = new TextBlock { Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        if (!isNew)
        {
            var delete = new Button { Content = "Delete contact", Style = S("Button.Secondary") };
            delete.SetResourceReference(Control.ForegroundProperty, "Brush.Danger");
            delete.Click += (_, _) =>
            {
                if (ChoiceDialog.Ask(w, $"Delete {contact.Display}?", "The contact is removed from your Google account too. Emails with them stay.", "Delete contact") != 0) return;
                engine.Contacts.Delete(contact);
                w.DialogResult = true;
            };
            DockPanel.SetDock(delete, Dock.Left);
            buttons.Children.Add(delete);
        }
        var ok = new Button { Content = "Save", IsDefault = true, Style = S("Button.Primary") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Style = S("Button.Secondary") };
        DockPanel.SetDock(ok, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        static List<string> Lines(string text) => text.Split('\n', ',', ';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        ok.Click += (_, _) =>
        {
            var edited = new SavedContact
            {
                Id = contact.Id, AccountId = where == null ? (isNew && contact.AccountId.Length == 0 ? accounts[0].Id : contact.AccountId) : accounts[where.SelectedIndex].Id,
                Resource = contact.Resource, Etag = contact.Etag, Groups = contact.Groups,
                GivenName = first.Text, FamilyName = last.Text, Emails = Lines(emails.Text), Phones = Lines(phones.Text),
                Company = company.Text, Title = title.Text, Notes = notes.Text,
            };
            ContactsService.Clean(edited);
            var bad = edited.Emails.FirstOrDefault(e => !MimeKit.MailboxAddress.TryParse(e, out _) || !e.Contains('@'));
            if (edited.Name.Length == 0 && edited.Emails.Count == 0) { Show(error, "Type a name or an email address."); return; }
            if (bad != null) { Show(error, $"\"{bad}\" isn't an email address."); return; }
            try
            {
                engine.Contacts.Save(edited);
                w.DialogResult = true;
            }
            catch (Exception ex) { Show(error, ex.Message); }
        };
        w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
        w.Loaded += (_, _) => first.Focus();
        w.ShowDialog();
    }

    private static void Show(TextBlock error, string text)
    {
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }
}
