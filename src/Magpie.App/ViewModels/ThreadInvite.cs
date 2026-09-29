using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

/// <summary>The invite card above a conversation (design B3): Accept / Maybe / Decline.</summary>
public partial class ThreadViewModel
{
    private CalendarInvite? _invite;
    private MessageRow? _inviteMessage;

    [ObservableProperty] private bool _hasInvite;
    [ObservableProperty] private string _inviteMonth = "";
    [ObservableProperty] private string _inviteDay = "";
    [ObservableProperty] private string _inviteWeekday = "";
    [ObservableProperty] private string _inviteTitle = "";
    [ObservableProperty] private string _inviteWhen = "";
    [ObservableProperty] private string _inviteWhere = "";
    [ObservableProperty] private string _inviteMeet = "";
    [ObservableProperty] private string _inviteWho = "";
    [ObservableProperty] private string _inviteClash = "";
    [ObservableProperty] private string _inviteStatus = "";
    [ObservableProperty] private bool _inviteCancelled;
    [ObservableProperty] private bool _canAnswerInvite;
    [ObservableProperty] private string _inviteAnswer = "";
    [ObservableProperty] private bool _addInviteNote;
    [ObservableProperty] private string _inviteNote = "";
    public bool HasInviteMeet => InviteMeet.Length > 0;
    public bool HasInviteWhere => InviteWhere.Length > 0;
    public bool HasInviteClash => InviteClash.Length > 0;
    public bool HasInviteStatus => InviteStatus.Length > 0;
    partial void OnInviteMeetChanged(string value) => OnPropertyChanged(nameof(HasInviteMeet));
    partial void OnInviteWhereChanged(string value) => OnPropertyChanged(nameof(HasInviteWhere));
    partial void OnInviteClashChanged(string value) => OnPropertyChanged(nameof(HasInviteClash));
    partial void OnInviteStatusChanged(string value) => OnPropertyChanged(nameof(HasInviteStatus));

    /// <summary>Finds the newest invite / update / cancellation in the conversation and fills the card.</summary>
    private async Task UpdateInviteAsync(List<MessageRow> rows, Dictionary<long, (MessageBody? body, Dictionary<string, string> images)> bodies, CancellationToken ct)
    {
        CalendarInvite? found = null;
        MessageRow? from = null;
        for (var i = rows.Count - 1; i >= 0 && found == null; i--)
        {
            var r = rows[i];
            var body = bodies.TryGetValue(r.Id, out var b) ? b.body : null;
            var ics = body?.Calendar ?? "";
            // Bodies saved before 1.2.0 don't keep the invite: read it from the saved message when it came as a file.
            if (ics.Length == 0 && body?.Attachments.Any(a => a.ContentType.Contains("calendar", StringComparison.OrdinalIgnoreCase)
                    || a.FileName.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) == true)
            {
                try
                {
                    var (_, mime) = await Task.Run(() => _e.LoadAsync(r, true, ct), ct);
                    if (mime != null) ics = MimeText.CalendarText(mime);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn("invite: " + ex.Message); }
            }
            var inv = Invites.Parse(ics);
            if (inv != null && (inv.IsRequest || inv.IsCancel)) { found = inv; from = r; }
        }
        ct.ThrowIfCancellationRequested();
        _invite = found;
        _inviteMessage = from;
        FillInvite();
    }

    private void FillInvite()
    {
        var inv = _invite;
        HasInvite = inv != null && HasThread;
        if (inv == null || _inviteMessage == null) return;
        var ev = _e.TrackInvite(AccountId, inv);
        var s = inv.Start.ToLocalTime();
        InviteMonth = s.ToString("MMM").ToUpperInvariant();
        InviteDay = s.Day.ToString();
        InviteWeekday = s.ToString("ddd");
        InviteTitle = string.IsNullOrWhiteSpace(inv.Summary) ? "(no title)" : inv.Summary;
        InviteWhen = Invites.WhenText(inv) + (inv.AllDay ? "" : " · " + (TimeZoneInfo.Local.IsDaylightSavingTime(s) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName));
        InviteWhere = inv.Location;
        InviteMeet = inv.MeetLink;
        var mine = _e.MyAddresses;
        var others = inv.Attendees.Count(a => !mine.Contains(a.Email, StringComparer.OrdinalIgnoreCase) && a.Email != inv.Organizer?.Email);
        var organiser = inv.Organizer == null ? "" : (mine.Contains(inv.Organizer.Email, StringComparer.OrdinalIgnoreCase) ? "You" : inv.Organizer.Display) + " (organiser)";
        InviteWho = string.Join(" · ", new[] { organiser, others == 0 ? "" : others == 1 ? "1 other guest" : $"{others} other guests" }.Where(x => x.Length > 0));
        InviteCancelled = inv.IsCancel || ev?.Cancelled == true;
        var iOrganise = inv.Organizer != null && mine.Contains(inv.Organizer.Email, StringComparer.OrdinalIgnoreCase);
        CanAnswerInvite = inv.IsRequest && !InviteCancelled && !iOrganise && inv.Organizer != null;
        InviteAnswer = ev?.Answer ?? "";
        InviteStatus = InviteCancelled ? "The organiser cancelled this event."
            : (ev?.Answer ?? "") switch
            {
                "ACCEPTED" => "You accepted",
                "TENTATIVE" => "You said maybe",
                "DECLINED" => "You declined",
                "UPDATED" => "The organiser changed the time — please answer again.",
                _ => "",
            };
        // Clashes with events you've said yes / maybe to in Magpie (there is no calendar sync yet, so this is all we know).
        var clash = InviteCancelled || inv.AllDay ? null : _e.Store.EventsOverlapping(inv.Start, inv.End, inv.Uid).FirstOrDefault();
        InviteClash = clash == null ? "" : $"Clashes with \"{clash.Summary}\" ({clash.Start.ToLocalTime():HH:mm}–{clash.End.ToLocalTime():HH:mm}), which you said yes or maybe to.";
    }

    [RelayCommand]
    private void AnswerInviteChoice(string? choice)
    {
        if (_invite == null || _inviteMessage == null || !Enum.TryParse<InviteAnswer>(choice, out var answer)) return;
        var inv = _invite;
        var msg = _inviteMessage;
        try
        {
            var (id, before) = _e.AnswerInvite(msg, inv, answer, AddInviteNote ? InviteNote : null);
            AddInviteNote = false;
            InviteNote = "";
            FillInvite();
            var seconds = _e.Config.UndoSendSeconds;
            if (seconds > 0 && System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel vm)
                vm.ShowActionToast($"Sending \"{Invites.SubjectFor(answer, inv.Summary)}\"", () =>
                {
                    if (!_e.UndoInviteAnswer(id, msg.AccountId, inv.Uid, before)) Ui.Error("Undo", "Too late — the answer has already been sent.");
                    if (_invite?.Uid == inv.Uid) FillInvite();
                }, seconds: seconds);
        }
        catch (Exception ex)
        {
            Log.Error("answer invite", ex);
            Ui.Error("Invite", ex.Message);
        }
    }

    [RelayCommand] private void OpenInviteMeet() { if (InviteMeet.Length > 0) Ui.OpenExternal(InviteMeet); }
}
