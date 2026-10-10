using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.Services;

/// <summary>
/// Design HM1: the hover cards of a reading page — an email address (E1–E11) or an attachment (A1–A9). The page asks
/// for a card ("hmopen"), this answers with its lines (hmShow), and runs the line picked ("hmdo").
/// Used by the main window's reading pane and by a conversation opened in its own window (S3).
/// </summary>
public sealed class ReaderHover
{
    private readonly Window _owner;
    private readonly MainViewModel _main;
    private readonly ThreadViewModel _reader;
    private readonly Func<string, Task> _script;
    /// <summary>Where each file was saved (A2), so the card can offer Show in folder (A7).</summary>
    private readonly Dictionary<(long Row, int Index), string> _saved = new();

    public ReaderHover(Window owner, MainViewModel main, ThreadViewModel reader, Func<string, Task> script)
    {
        _owner = owner;
        _main = main;
        _reader = reader;
        _script = script;
    }

    private MailEngine E => AppServices.Engine;
    private bool HasCalendar => _main.Cal.WritableCalendars().Count > 0;
    /// <summary>Design B4: E6 "Add to contacts" when a Google account can save contacts.</summary>
    private bool HasContacts => E.Contacts.GoogleAccounts.Count > 0;

    private Task Run(string js) { try { return _script(js); } catch (Exception ex) { Log.Warn("hover card: " + ex.Message); return Task.CompletedTask; } }
    private Task Note(string text) => Run("hmNote(" + JsonSerializer.Serialize(text) + ")");
    private Task Hide() => Run("hmHide()");

    private static string Str(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Num(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>A message from the reading page: its own buttons (reply, attachment, link…) and the hover cards.</summary>
    public void OnMessage(JsonElement root)
    {
        var t = Str(root, "t");
        long id = Num(root, "id");
        switch (t)
        {
            case "link": _reader.OnLink(Str(root, "href")); break;
            case "reply": _reader.Compose(ComposeMode.Reply, id); break;
            case "replyall": _reader.Compose(ComposeMode.ReplyAll, id); break;
            case "forward": _reader.Compose(ComposeMode.Forward, id); break;
            case "att": _ = _reader.OpenAttachmentAsync(id, (int)Num(root, "i")); break;
            case "retry": _reader.RetryLoad(); break;
            case "hmopen": Open(root.GetProperty("c").Clone()); break;
            case "hmdo": Do(Str(root, "id"), root.GetProperty("c").Clone()); break;
        }
    }

    // ───────────────────────── subject card (S1–S9) ─────────────────────────

    /// <summary>What the subject card shows; S3 is left out in a conversation that already has its own window.</summary>
    public SubjectCard.Content? SubjectContent(bool ownWindow)
    {
        if (!_reader.HasThread) return null;
        var items = HoverMenus.ForSubject(HasCalendar, !ownWindow && _reader.ShowSummarise).Where(i => !(ownWindow && i.Id == "S3")).ToList();
        return new SubjectCard.Content(HoverMenus.CleanSubject(_reader.Subject), HoverMenus.SubjectLine(_reader.Messages.Count, _reader.FolderName), items);
    }

    public void RunSubject(string id, FrameworkElement anchor, SubjectCard card)
    {
        try
        {
            var subject = HoverMenus.CleanSubject(_reader.Subject);
            switch (id)
            {
                case "S1":
                    System.Windows.Clipboard.SetText(subject);
                    card.Note("Copied the subject");
                    return;
                case "S2": card.Close(); Search(HoverMenus.SearchFor(id, subject)); return;
                case "S3": card.Close(); if (_reader.CurrentThreadRow is { } row) ThreadWindow.Open(_main, row); return;
                case "S4": card.Close(); ShowMenu(anchor, ReaderMenus.Remind(_reader, _owner)); return;
                case "S5": card.Close(); ShowMenu(anchor, ReaderMenus.Snooze(_reader, _owner)); return;
                case "S6": card.Close(); ShowMenu(anchor, ReaderMenus.Tag(_reader)); return;
                case "S7":
                    card.Close();
                    if (!_main.Cal.NewEventWith(subject, _reader.People(), $"From the email \"{subject}\""))
                        Ui.Error("Calendar", "Sign in to a Google account (Settings → Accounts) so Magpie can add events to its calendar.", _owner);
                    return;
                case "S8": card.Close(); SettingsWindow.OpenNewRule(RuleField.Subject, subject, "Subject: " + subject); return;
                case "S9": card.Close(); _reader.SummariseCommand.Execute(null); return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("subject action " + id, ex);
            card.Close();
            Ui.Error("Magpie", ex.Message, _owner);
        }
    }

    private static void ShowMenu(FrameworkElement anchor, System.Windows.Controls.ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The page wants the card for an address or a file.</summary>
    private async void Open(JsonElement c)
    {
        try
        {
            object card;
            if (Str(c, "k") == "a")
            {
                var address = Str(c, "a").Trim();
                if (!address.Contains('@')) return;
                var name = Str(c, "n").Trim().Trim('"');
                var isMe = _reader.IsMyAddress(address);
                var (count, last) = await Task.Run(() => E.Store.AddressStats(address));
                var saved = E.Store.GetSavedContacts().Any(sc => sc.Emails.Contains(address, StringComparer.OrdinalIgnoreCase));
                var items = HoverMenus.ForAddress(name, address, isMe, HasCalendar, _reader.PicturesTrusted(address), HasContacts, saved);
                card = new
                {
                    title = name.Length > 0 ? name : address,
                    sub = HoverMenus.AddressLine(address, count, last, DateTimeOffset.Now),
                    badge = HtmlRenderer.Initials(name.Length > 0 ? name : address),
                    file = false,
                    items = items.Select(i => new { id = i.Id, label = i.Label }),
                };
            }
            else
            {
                var rowId = Num(c, "m");
                var index = (int)Num(c, "i");
                if (_reader.RowById(rowId) is not { } row) return;
                var files = _reader.AttachmentsOf(rowId);
                var info = files.FirstOrDefault(a => a.Index == index);
                var fileName = info?.FileName ?? Str(c, "f");
                var items = HoverMenus.ForAttachment(fileName, row.FromName, row.FromAddress, _reader.IsMyAddress(row.FromAddress), files.Count, _saved.ContainsKey((rowId, index)));
                card = new
                {
                    title = fileName,
                    sub = HoverMenus.AttachmentLine(fileName, info?.Size ?? 0, E.HasMessageFile(row)),
                    badge = HoverMenus.Badge(fileName),
                    file = true,
                    items = items.Select(i => new { id = i.Id, label = i.Label }),
                };
            }
            await Run("hmShow(" + JsonSerializer.Serialize(card) + ")");
        }
        catch (Exception ex) { Log.Warn("hover card: " + ex.Message); }
    }

    /// <summary>A line of the card was picked.</summary>
    private async void Do(string id, JsonElement c)
    {
        try
        {
            if (id.StartsWith('E')) await DoAddress(id, Str(c, "a").Trim(), Str(c, "n").Trim().Trim('"'), Num(c, "m"));
            else if (id.StartsWith('A')) await DoFile(id, Num(c, "m"), (int)Num(c, "i"));
        }
        catch (Exception ex)
        {
            Log.Error("hover action " + id, ex);
            await Hide();
            Ui.Error("Magpie", ex.Message, _owner);
        }
    }

    private async Task DoAddress(string id, string address, string name, long rowId)
    {
        var who = HoverMenus.FirstName(name, address);
        var accountId = _reader.RowById(rowId)?.AccountId ?? _reader.AccountId;
        switch (id)
        {
            case "E1":
                System.Windows.Clipboard.SetText(address);
                await Note("Copied " + address);
                break;
            case "E2":
                var both = HoverMenus.NameAndAddress(name, address);
                System.Windows.Clipboard.SetText(both);
                await Note("Copied " + both);
                break;
            case "E3":
                await Hide();
                ComposeWindow.OpenMailto("mailto:" + address, accountId);
                break;
            case "E4":
            case "E5":
                await Hide();
                Search(HoverMenus.SearchFor(id, address));
                break;
            case "E6":
                await Hide();
                if (E.Store.GetSavedContacts().Any(sc => sc.Emails.Contains(address, StringComparer.OrdinalIgnoreCase)))
                    _main.OpenContact(address);   // already saved: show it
                else
                    Views.ContactEditWindow.Edit(_owner, _main.People.NewFrom(name, address));
                break;
            case "E7":
                await Hide();
                if (!_main.Cal.NewEventWith("", new[] { (name, address) }))
                    Ui.Error("Calendar", "Sign in to a Google account (Settings → Accounts) so Magpie can add events to its calendar.", _owner);
                break;
            case "E8":
                _reader.TrustAddress(address);
                await Note($"Pictures from {who} will load from now on");
                break;
            case "E9":
                await Hide();
                SettingsWindow.OpenNewRule(RuleField.From, address, "Emails from " + who);
                break;
            case "E10":
                await Hide();
                AutoDeleteDialog.Show(_owner, new AutoDeleteRule { Pattern = address.ToLowerInvariant() }, editing: false);
                break;
            case "E11":
                await Hide();
                if (Ui.Confirm("Block", $"Send emails from {address} to Spam, now and later?\n\nNothing is deleted. You can unblock them in Gatekeeper.", _owner))
                    E.BlockSender(address);
                break;
        }
    }

    private async Task DoFile(string id, long rowId, int index)
    {
        if (_reader.RowById(rowId) is not { } row) return;
        switch (id)
        {
            case "A1":
                await Hide();
                await _reader.OpenAttachmentAsync(rowId, index);
                break;
            case "A5":
            {
                await Note("Getting the file…");
                if (await _reader.ExtractAttachmentAsync(rowId, index) is not { } f) return;
                await Hide();
                PreviewWindow.Show(_owner, f.Path, f.Name);
                break;
            }
            case "A2":
            {
                await Note("Getting the file…");
                if (await _reader.ExtractAttachmentAsync(rowId, index) is not { } f) return;
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = f.Name, InitialDirectory = Downloads };
                if (dlg.ShowDialog(_owner) != true) { await Note(""); return; }
                File.Copy(f.Path, dlg.FileName, true);
                _saved[(rowId, index)] = dlg.FileName;
                await Note("Saved to " + Path.GetFileName(Path.GetDirectoryName(dlg.FileName)) + " · Show in folder is now on this card");
                break;
            }
            case "A3":
            {
                var files = _reader.AttachmentsOf(rowId);
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Save {files.Count} attachments to", InitialDirectory = Downloads };
                if (dlg.ShowDialog(_owner) != true) return;
                await Note("Saving…");
                int n = 0;
                foreach (var a in files)
                {
                    if (await _reader.ExtractAttachmentAsync(rowId, a.Index) is not { } f) continue;
                    var dest = UniquePath(dlg.FolderName, f.Name);
                    File.Copy(f.Path, dest);
                    _saved[(rowId, a.Index)] = dest;
                    n++;
                }
                await Note($"Saved {n} file{(n == 1 ? "" : "s")} to {Path.GetFileName(dlg.FolderName.TrimEnd('\\'))}");
                break;
            }
            case "A4":
            {
                await Note("Getting the file…");
                if (await _reader.ExtractAttachmentAsync(rowId, index) is not { } f) return;
                System.Windows.Clipboard.SetFileDropList(new StringCollection { f.Path });
                await Note("Copied — paste it into a folder or another app");
                break;
            }
            case "A6":
                await Hide();
                ComposeWindow.OpenForwardOnly(row, index);
                break;
            case "A7":
                await Hide();
                if (_saved.TryGetValue((rowId, index), out var path) && File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else Ui.Error("Show in folder", "That copy was moved or deleted.", _owner);
                break;
            case "A8":
                await Hide();
                Search(HoverMenus.SearchFor(id, row.FromAddress));
                break;
            case "A9":
            {
                await Hide();
                var name = _reader.AttachmentsOf(rowId).FirstOrDefault(a => a.Index == index)?.FileName ?? "";
                if (name.Length > 0) Search(HoverMenus.SearchFor(id, name));
                break;
            }
        }
    }

    private void Search(string text)
    {
        _main.SearchEverywhere(text);
        if (Application.Current.MainWindow is { } w && w != _owner) { if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); }
    }

    private static string Downloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }
}
