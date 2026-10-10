using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Contacts;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.App.ViewModels;

/// <summary>A row of the contacts list (design B4): a section heading ("A", "Family") or a person.</summary>
public sealed class ContactListItem
{
    public string Header { get; init; } = "";
    public bool IsHeader => Header.Length > 0;
    public ContactEntry? Entry { get; init; }
    public string Name => Entry?.Name ?? "";
    public string Email => Entry?.Email ?? "";
    public bool IsRecent => Entry?.IsRecent == true;
    public string Initials => Entry == null ? "" : ThreadItem.MakeInitials(Entry.Name);
    public System.Windows.Media.Brush? Avatar => Entry == null ? null : ContactsViewModel.AvatarFor(Entry.Email);
}

/// <summary>A conversation, file or event listed under a contact.</summary>
public sealed class ContactActivity
{
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public string When { get; init; } = "";
    public ThreadRow? Thread { get; init; }
    public CalendarEvent? Event { get; init; }
}

/// <summary>Design B4: the Contacts page — list on the left, the chosen person on the right.</summary>
public partial class ContactsViewModel : ObservableObject
{
    private readonly MailEngine _e = AppServices.Engine;
    private List<ContactEntry> _entries = new();

    public ObservableCollection<ContactListItem> Items { get; } = new();
    public ObservableCollection<ContactActivity> Activity { get; } = new();

    [ObservableProperty] private string _search = "";
    /// <summary>"all", "frequent" or "groups".</summary>
    [ObservableProperty] private string _tab = "all";
    [ObservableProperty] private ContactListItem? _selected;
    /// <summary>"conversations", "attachments" or "events".</summary>
    [ObservableProperty] private string _activityTab = "conversations";
    [ObservableProperty] private string _allLabel = "All";
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private bool _problemNeedsSignIn;
    public string ProblemAccountId { get; private set; } = "";

    // The chosen person
    [ObservableProperty] private bool _hasPerson;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _work = "";
    [ObservableProperty] private string _initials = "";
    [ObservableProperty] private System.Windows.Media.Brush? _avatar;
    [ObservableProperty] private string _emailsText = "";
    [ObservableProperty] private string _phonesText = "";
    [ObservableProperty] private string _groupsText = "";
    [ObservableProperty] private string _savedIn = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _isSaved;
    [ObservableProperty] private string _conversationsLabel = "Conversations";
    [ObservableProperty] private string _attachmentsLabel = "Attachments";
    [ObservableProperty] private string _eventsLabel = "Events";
    [ObservableProperty] private string _activityEmpty = "";

    /// <summary>Open a conversation (in its own window), an event, or the edit window — handled by the view.</summary>
    public event Action<ThreadRow>? OpenThread;
    public event Action<CalendarEvent>? OpenEvent;
    public event Action<SavedContact>? EditContact;

    public bool CanSave => _e.Contacts.GoogleAccounts.Count > 0;

    public ContactsViewModel()
    {
        _e.Contacts.Changed += () => Ui.Post(Refresh);
    }

    public void Refresh()
    {
        var keep = Selected?.Entry?.Key;
        _entries = ContactSuggest.Entries(_e.Store.GetSavedContacts(), _e.Store.MailPeople());
        AllLabel = $"All {_entries.Count:N0}";
        var problem = _e.Contacts.Problems.FirstOrDefault();
        var p = problem.Value;
        ProblemAccountId = problem.Key ?? "";
        Problem = p?.Message ?? (CanSave ? "" : "Sign in to a Google account (Settings → Accounts) to see and save its contacts. People you wrote to are listed as Recent.");
        ProblemNeedsSignIn = p?.NeedsSignIn == true;
        BuildList(keep);
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnSearchChanged(string value) => BuildList(Selected?.Entry?.Key);
    partial void OnTabChanged(string value) => BuildList(Selected?.Entry?.Key);
    partial void OnSelectedChanged(ContactListItem? value)
    {
        // A section heading can't be chosen: the person under it is.
        if (value is { IsHeader: true })
        {
            var i = Items.IndexOf(value);
            Selected = Items.Skip(i + 1).FirstOrDefault(x => !x.IsHeader) ?? Items.Take(i).LastOrDefault(x => !x.IsHeader);
            return;
        }
        ShowPerson(value?.Entry);
    }
    partial void OnActivityTabChanged(string value) => LoadActivity();

    [RelayCommand] private void SetTab(string tab) => Tab = tab;
    [RelayCommand] private void SetActivityTab(string tab) => ActivityTab = tab;

    private void BuildList(string? keepKey)
    {
        var shown = Search.Trim().Length == 0 ? _entries : ContactSuggest.Entries(_e.Store.GetSavedContacts(), _e.Store.MailPeople(), Search);
        Items.Clear();
        switch (Tab)
        {
            case "frequent":
                foreach (var x in ContactSuggest.Frequent(shown)) Items.Add(new ContactListItem { Entry = x });
                break;
            case "groups":
                foreach (var (group, members) in ContactSuggest.Groups(shown))
                {
                    Items.Add(new ContactListItem { Header = $"{group} · {members.Count}" });
                    foreach (var m in members) Items.Add(new ContactListItem { Entry = m });
                }
                break;
            default:
                string? section = null;
                foreach (var x in SavedFirst(shown))
                {
                    var s = x.IsRecent ? "Recent · not saved" : x.Section;
                    if (s != section) { Items.Add(new ContactListItem { Header = s }); section = s; }
                    Items.Add(new ContactListItem { Entry = x });
                }
                break;
        }
        Selected = Items.FirstOrDefault(i => i.Entry?.Key == keepKey) ?? Items.FirstOrDefault(i => !i.IsHeader);
    }

    // Recent people come last in "All": sort them into their own block after the saved ones.
    private static IEnumerable<ContactEntry> SavedFirst(IEnumerable<ContactEntry> list) => list.OrderBy(e => e.IsRecent);

    private ContactEntry? _person;

    private void ShowPerson(ContactEntry? x)
    {
        _person = x;
        HasPerson = x != null;
        if (x == null) { Activity.Clear(); return; }
        Name = x.Name;
        Initials = ThreadItem.MakeInitials(x.Name);
        Avatar = AvatarFor(x.Email);
        var c = x.Saved;
        IsSaved = c != null;
        Work = c?.Work ?? "";
        EmailsText = string.Join("\n", c?.Emails is { Count: > 0 } es ? es : new List<string> { x.Email });
        PhonesText = c == null || c.Phones.Count == 0 ? "—" : string.Join("\n", c.Phones);
        GroupsText = c == null || c.Groups.Count == 0 ? "—" : string.Join(", ", c.Groups);
        SavedIn = c == null ? "Not saved · you wrote to them " + Times(x.Count)
                  : (_e.AccountById(c.AccountId)?.Email ?? "Google") + (c.Pending != PendingContactOp.None ? " · saving to Google…" : "");
        Notes = c?.Notes ?? "";
        LoadActivity();
    }

    private static string Times(int n) => n == 1 ? "once" : $"{n:N0} times";

    private IEnumerable<string> Addresses() =>
        _person?.Saved?.Emails is { Count: > 0 } es ? es : _person == null ? Enumerable.Empty<string>() : new[] { _person.Email };

    private void LoadActivity()
    {
        Activity.Clear();
        if (_person == null) return;
        var addresses = Addresses().ToList();
        var now = DateTimeOffset.Now;
        var folders = _e.AllMailFolderIds();
        List<ThreadRow> Threads(string extra) =>
            addresses.SelectMany(a => _e.Store.ListThreads(new ListQuery { FolderIds = folders, Search = SearchQuery.Parse($"with:{a} {extra}"), Limit = 50 }, now))
                     .DistinctBy(t => t.AccountId + "|" + t.ThreadKey).OrderByDescending(t => t.Latest.Date).ToList();
        var conversations = Threads("");
        var files = Threads("has:attachment");
        var events = _e.Calendar.Between(now.AddDays(-60), now.AddDays(400))
            .Where(ev => ev.Attendees.Any(at => addresses.Contains(at.Email, StringComparer.OrdinalIgnoreCase))
                         || addresses.Contains(ev.Organizer, StringComparer.OrdinalIgnoreCase))
            .OrderBy(ev => ev.Start).ToList();
        ConversationsLabel = $"Conversations {Count(conversations.Count)}";
        AttachmentsLabel = $"Attachments {Count(files.Count)}";
        EventsLabel = $"Events {events.Count}";
        switch (ActivityTab)
        {
            case "attachments":
                foreach (var t in files) Activity.Add(new ContactActivity { Title = Subject(t), Sub = "📎 " + t.Latest.Sender, When = HtmlRendererDate(t.Latest.Date, now), Thread = t });
                ActivityEmpty = "No emails with files from or to them on this PC.";
                break;
            case "events":
                foreach (var ev in events)
                    Activity.Add(new ContactActivity { Title = ev.Title.Length > 0 ? ev.Title : "(no title)", Sub = ev.Location,
                        When = ev.Start.ToLocalTime().ToString(ev.AllDay ? "d MMM yyyy" : "d MMM yyyy, HH:mm", CultureInfo.InvariantCulture), Event = ev });
                ActivityEmpty = "No events with them in your calendars.";
                break;
            default:
                foreach (var t in conversations) Activity.Add(new ContactActivity { Title = Subject(t), Sub = t.Latest.Preview, When = HtmlRendererDate(t.Latest.Date, now), Thread = t });
                ActivityEmpty = "No emails with them on this PC.";
                break;
        }
    }

    private static string Count(int n) => n >= 50 ? "50+" : n.ToString(CultureInfo.InvariantCulture);
    private static string Subject(ThreadRow t) => string.IsNullOrWhiteSpace(t.Latest.Subject) ? "(no subject)" : t.Latest.Subject;
    private static string HtmlRendererDate(DateTimeOffset d, DateTimeOffset now) => Magpie.Core.Mail.HtmlRenderer.FriendlyDate(d, now);

    [RelayCommand]
    private void OpenActivity(ContactActivity? a)
    {
        if (a?.Thread is { } t) OpenThread?.Invoke(t);
        else if (a?.Event is { } ev) OpenEvent?.Invoke(ev.Clone());
    }

    [RelayCommand]
    private void NewMessage()
    {
        if (_person == null) return;
        var to = Magpie.Core.Mail.HoverMenus.NameAndAddress(_person.Name, Addresses().First());
        Views.ComposeWindow.OpenMailto("mailto:" + Uri.EscapeDataString(to));
    }

    [RelayCommand]
    private void NewEvent(MainViewModel? main)
    {
        if (_person == null || main == null) return;
        if (!main.Cal.NewEventWith("", new[] { (_person.Name, Addresses().First()) }))
            Ui.Error("Calendar", "Sign in to a Google account (Settings → Accounts) so Magpie can add events to its calendar.");
    }

    /// <summary>Edit a saved contact, or save someone from Recent (design B4, and the address card's E6).</summary>
    [RelayCommand]
    private void Edit()
    {
        if (_person == null) return;
        EditContact?.Invoke(_person.Saved is { } s ? _e.Store.GetSavedContact(s.Id) ?? s : NewFrom(_person.Name, _person.Email));
    }

    [RelayCommand] private void NewContact() => EditContact?.Invoke(NewFrom("", ""));

    public SavedContact NewFrom(string name, string email)
    {
        var parts = (name ?? "").Trim().Trim('"').Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var account = _e.Contacts.GoogleAccounts.FirstOrDefault();
        return new SavedContact
        {
            AccountId = account?.Id ?? "",
            GivenName = parts.Length > 0 && !parts[0].Contains('@') ? parts[0] : "",
            FamilyName = parts.Length > 1 ? parts[1] : "",
            Emails = email.Length > 0 ? new List<string> { email } : new(),
        };
    }

    public void SelectAddress(string address)
    {
        var item = Items.FirstOrDefault(i => i.Entry is { } x && (x.Email.Equals(address, StringComparison.OrdinalIgnoreCase)
                                                                  || x.Saved?.Emails.Contains(address, StringComparer.OrdinalIgnoreCase) == true));
        if (item != null) Selected = item;
    }

    private static readonly string[] Palette = { "#2563EB", "#B91C1C", "#EA580C", "#6D28D9", "#0F766E", "#A21CAF", "#334155", "#0369A1", "#15803D", "#B45309", "#BE185D", "#4338CA" };

    public static System.Windows.Media.Brush AvatarFor(string address)
    {
        uint h = 2166136261;
        foreach (var ch in (address ?? "").ToLowerInvariant()) { h ^= ch; h *= 16777619; }
        return Icons.Brush(Palette[(int)(h % (uint)Palette.Length)]);
    }
}
