using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.App.ViewModels;

/// <summary>Design PE1: one of the four filters above the "emails with one address" list.</summary>
public sealed partial class PersonFilterItem : ObservableObject
{
    public PersonKind Kind { get; init; }
    public string Tip { get; init; } = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Design PE1: every email with one address — opened from the address card (All emails with / Emails from / Emails you
/// sent to). The list sits on the Inbox view, which already looks in every folder, and replaces its heading and search
/// chips with its own: Back, the person, four filters and the Each email / Conversations switch.
/// </summary>
public partial class MainViewModel
{
    private string _personAddress = "";
    private string _personName = "";
    /// <summary>True while this code changes the view or the search box itself (those changes must not end the list).</summary>
    private bool _personSetting;
    private (string? NavKey, string Search, string? Selected, string Label) _personBack;
    /// <summary>Goes up when a person list opens or ends: a server search that comes back later is for another list.</summary>
    private int _personSession;
    private int _personStatsGen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotPersonView), nameof(PersonRowsShown), nameof(ShowListTools), nameof(RowActionsAlways), nameof(RowActionsOnHover), nameof(ShowGateBanner))]
    private bool _isPersonView;

    /// <summary>One row per email (the default); off = one row per conversation, with ticks and row buttons as usual.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonConversations), nameof(PersonRowsShown), nameof(ShowListTools), nameof(RowActionsAlways), nameof(RowActionsOnHover))]
    private bool _personEachEmail = true;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PersonBinButton))] private bool _personIncludeBin;
    [ObservableProperty] private string _personTitle = "";
    [ObservableProperty] private string _personSubTitle = "";
    [ObservableProperty] private string _personBackText = "Back";
    [ObservableProperty] private string _personFootText = "";
    /// <summary>The line about the server: older emails still arriving, searching, or what the search found.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PersonShowServer))] private string _personServerText = "";
    [ObservableProperty] private bool _personCanAskServer;
    private bool _personAsking;
    private string _personServerResult = "";

    public ObservableCollection<PersonFilterItem> PersonFilters { get; } = new();
    public PersonKind PersonKind { get; private set; }
    public bool IsNotPersonView => !IsPersonView;
    public bool PersonConversations => !PersonEachEmail;
    /// <summary>The list shows one row per email: no tick boxes, no row buttons (those act on whole conversations).</summary>
    public bool PersonRowsShown => IsPersonView && PersonEachEmail;
    /// <summary>The tick box, Select ▾ and the count above the list.</summary>
    public bool ShowListTools => !IsScheduledView && !PersonRowsShown;
    public bool PersonShowServer => PersonServerText.Length > 0;
    public string PersonBinButton => PersonIncludeBin ? "Leave them out" : "Include them";
    private string PersonScope => $"person\n{_personAddress}\n{PersonKind}\n{PersonEachEmail}\n{PersonIncludeBin}";

    /// <summary>Sets the search box without ending the person list and without a second list reload.</summary>
    private void SetSearchQuietly(string text)
    {
        var was = _personSetting;
        _personSetting = true;
        try { SearchText = text; }
        finally { _personSetting = was; }
    }

    /// <summary>Opens the list for this address (from the address card; <paramref name="name"/> may be empty).</summary>
    public void ShowPerson(string address, string name, PersonKind kind)
    {
        address = (address ?? "").Trim();
        if (!address.Contains('@')) return;
        if (!IsPersonView)
            _personBack = (Current?.Key, SearchText, Selected?.SelectKey, IsCalendarView ? "Calendar" : IsContactsView ? "Contacts" : Current?.Label ?? "Inbox");
        _personAddress = address;
        _personName = (name ?? "").Trim().Trim('"');
        PersonKind = kind;
        _personServerResult = "";
        _personSession++;
        _personSetting = true;
        try
        {
            IsCalendarView = false;
            IsContactsView = false;
            AllInView = false;
            var inbox = Smart.FirstOrDefault(n => n.Kind == NavKind.Inbox);
            IsPersonView = true;
            if (inbox != null && Current != inbox) Current = inbox;
            SearchText = "with:" + address;
        }
        finally { _personSetting = false; }
        PersonTitle = PersonView.Title(_personName, _personAddress);
        PersonSubTitle = PersonView.SubTitle(_personName, _personAddress, _e.Accounts.Count);
        PersonBackText = "‹ Back to " + _personBack.Label;
        PersonFilters.Clear();
        foreach (var k in PersonView.Kinds)
            PersonFilters.Add(new PersonFilterItem
            {
                Kind = k, IsSelected = k == kind, Tip = PersonView.FilterTip(k, _personName, _personAddress),
                Label = PersonView.FilterWords(k, _personName, _personAddress),
            });
        Selected = null;
        ClearSelection();
        ReloadList();
    }

    /// <summary>Ends the person list where it is (the caller decides what the list shows next).</summary>
    private void LeavePerson()
    {
        if (!IsPersonView) return;
        _personSession++;
        _personStatsGen++;
        IsPersonView = false;
        PersonServerText = "";
        PersonCanAskServer = false;
    }

    /// <summary>"Back to …": the folder, search and conversation that were open before.</summary>
    [RelayCommand]
    private void PersonBack()
    {
        if (!IsPersonView) return;
        var back = _personBack;
        LeavePerson();
        _personSetting = true;
        try
        {
            SearchText = back.Search;
            var nav = Smart.Concat(AccountNodes.SelectMany(n => n.Folders)).Concat(TagItems).FirstOrDefault(n => n.Key == back.NavKey) ?? Current;
            if (nav != null && nav != Current) Current = nav; else ReloadList();
        }
        finally { _personSetting = false; }
        if (back.Selected != null && Threads.FirstOrDefault(t => t.SelectKey == back.Selected) is { } again) Selected = again;
    }

    [RelayCommand]
    private void SetPersonKind(PersonFilterItem? filter)
    {
        if (filter == null || !IsPersonView) return;
        PersonKind = filter.Kind;
        foreach (var f in PersonFilters) f.IsSelected = f.Kind == filter.Kind;
        Selected = null;
        ReloadList();
    }

    /// <summary>"each" = one row per email; anything else = conversations.</summary>
    [RelayCommand]
    private void SetPersonRows(string? rows)
    {
        var each = rows == "each";
        if (!IsPersonView || PersonEachEmail == each) return;
        PersonEachEmail = each;
        Selected = null;
        ClearSelection();
        ReloadList();
    }

    [RelayCommand]
    private void TogglePersonBin()
    {
        if (!IsPersonView) return;
        PersonIncludeBin = !PersonIncludeBin;
        ReloadList();
    }

    /// <summary>"Search the server too": lists the emails with this address that are not on this PC yet.</summary>
    [RelayCommand]
    private async Task PersonSearchServer()
    {
        if (!IsPersonView || _personAsking) return;
        var session = _personSession;
        var address = _personAddress;
        _personAsking = true;
        PersonCanAskServer = false;
        PersonServerText = "Searching the server…";
        try
        {
            var (added, problem) = await _e.FindAddressOnServerAsync(address);
            if (session == _personSession) _personServerResult = PersonView.ServerResult(added, problem);
        }
        catch (Exception ex)
        {
            Log.Warn("search the server: " + ex.Message);
            if (session == _personSession) _personServerResult = "Could not search the server: " + ex.Message;
        }
        finally { _personAsking = false; }
        if (session == _personSession && IsPersonView) ReloadList();
    }

    /// <summary>The same emails as conversations (Conversations switch, Select all N, the count above the list).</summary>
    private ListQuery PersonQuery(int limit) => new()
    {
        FolderIds = _e.PersonFolderIds(PersonIncludeBin),
        Search = SearchQuery.ForPerson(_personAddress, PersonKind, _e.MyAddresses),
        AnySnooze = true,
        Limit = limit,
    };

    /// <summary>One row per email: who it went from and to, its folder, and marks for Bcc.</summary>
    private List<ThreadItem> PersonEmailItems(DateTimeOffset now, int limit)
    {
        var multi = _e.Accounts.Count > 1;
        return _e.PersonEmails(_personAddress, PersonKind, PersonIncludeBin, limit).Select(email =>
        {
            var m = email.Row;
            var acc = _e.AccountById(m.AccountId);
            var row = new ThreadRow
            {
                AccountId = m.AccountId, ThreadKey = m.ThreadKey, Latest = m, Count = 1, UnreadCount = m.IsSeen ? 0 : 1,
                Flagged = m.IsFlagged, HighPriority = m.IsHighPriority, HasAttachments = m.HasAttachments, SnoozeUntil = m.SnoozeUntil,
            };
            var marks = new List<TagChip> { TagChip.Plain(PersonView.FolderLabel(email.FolderRole, email.FolderPath), "#5A6068") };
            if (PersonView.OnlyBcc(email, _personAddress)) marks.Add(TagChip.Plain("Bcc", "#6D28D9"));
            var (glyph, colour, tip) = email.Kind switch
            {
                PersonKind.From => ("↙", "#0F766E", "Received"),
                PersonKind.YouSent => ("↗", "#6D28D9", "You sent this"),
                _ => ("cc", "#334155", "Someone else sent this to both of you"),
            };
            return new ThreadItem(row, acc?.Color ?? "#14606E", multi, now, acc?.Email ?? "")
            {
                PersonRowId = m.Id,
                Sender = PersonView.RowLine(email, _personName, _personAddress),
                Initials = glyph,
                AvatarBrush = Icons.Brush(colour),
                AvatarTip = tip,
                TagChips = marks,
            };
        }).ToList();
    }

    /// <summary>After the list loaded: the empty text, the line under the list, and the numbers on the filters.</summary>
    private void RefreshPersonList(int shown, bool more)
    {
        EmptyText = Threads.Count > 0 ? "" : PersonView.EmptyText(PersonKind, _personName, _personAddress);
        PersonFootText = PersonView.FootLine(shown, more, PersonIncludeBin, PersonConversations);
        var older = _e.OlderEmailsLeft;
        PersonCanAskServer = older && !_personAsking;
        PersonServerText = _personAsking ? "Searching the server…"
            : _personServerResult.Length > 0 ? _personServerResult
            : older ? "An account is still getting older emails, so more may appear."
            : "";
        var gen = ++_personStatsGen;
        var (address, name, bin) = (_personAddress, _personName, PersonIncludeBin);
        _ = Task.Run(() => _e.PersonStats(address, bin)).ContinueWith(t =>
        {
            if (t.IsFaulted) { Log.Warn("person numbers: " + t.Exception?.GetBaseException().Message); return; }
            Ui.Post(() =>
            {
                if (gen != _personStatsGen || !IsPersonView) return;
                foreach (var f in PersonFilters) f.Label = PersonView.FilterLabel(f.Kind, name, address, t.Result);
            });
        });
    }
}
