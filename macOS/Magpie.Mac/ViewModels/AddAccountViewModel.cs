using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.Core;
using Magpie.Core.Auth;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Mac.Services;

namespace Magpie.Mac.ViewModels;

public enum AccountChoice { Automatic, Google, Microsoft, Other }

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Add an account (or sign in again): email + password for IMAP/SMTP, with server settings from Core's
/// ProviderPresets (and autoconfig lookup), or "Sign in with Google / Microsoft" through Core's OAuth (the browser
/// opens with <c>open</c>; Magpie listens on a loopback port for the answer). The same steps as the Windows window.
/// </summary>
public sealed partial class AddAccountViewModel : ObservableObject
{
    private static MailEngine E => AppServices.Engine;
    private readonly Account? _reauth;
    private CancellationTokenSource? _cts;
    private string _lastLookup = "";

    public bool IsReauth => _reauth != null;
    public string AddButtonText => IsReauth ? "Save and sign in" : "Add account";
    public string Heading => IsReauth ? "Sign in again" : E.Accounts.Count == 0 ? "Welcome to Magpie" : "Add an account";
    public string SubHeading => IsReauth ? $"{_reauth!.Email} needs you to sign in again." :
        "Gmail, Outlook / Microsoft 365 or any IMAP mailbox. Everything stays on this Mac.";

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private Choice<AccountChoice>? _choice;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private string _imapHost = "";
    [ObservableProperty] private int _imapPort = 993;
    [ObservableProperty] private Choice<TlsMode>? _imapSecurity;
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private int _smtpPort = 465;
    [ObservableProperty] private Choice<TlsMode>? _smtpSecurity;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private bool _useAppPassword;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _password = "";

    public event Action? Done;

    public List<Choice<AccountChoice>> Choices { get; } = new()
    {
        new(AccountChoice.Automatic, "Detect from the address"),
        new(AccountChoice.Google, "Google (Gmail or Workspace)"),
        new(AccountChoice.Microsoft, "Microsoft (Outlook.com or Microsoft 365)"),
        new(AccountChoice.Other, "Other (IMAP / SMTP)"),
    };

    public List<Choice<TlsMode>> SecurityChoices { get; } = new()
    {
        new(TlsMode.SslOnConnect, "SSL/TLS"),
        new(TlsMode.StartTls, "STARTTLS"),
        new(TlsMode.None, "None (not recommended)"),
    };

    public AddAccountViewModel(Account? reauth)
    {
        _reauth = reauth;
        _choice = Choices[0];
        _imapSecurity = SecurityChoices[0];
        _smtpSecurity = SecurityChoices[0];
        if (reauth != null)
        {
            _email = reauth.Email;
            _displayName = reauth.DisplayName;
            _choice = Choices[reauth.Kind switch { AccountKind.Gmail => 1, AccountKind.Microsoft => 2, _ => 3 }];
            _useAppPassword = reauth.Kind == AccountKind.Gmail && reauth.Auth == AuthMethod.Password;
            _imapHost = reauth.ImapHost; _imapPort = reauth.ImapPort; _imapSecurity = Sec(reauth.ImapSecurity);
            _smtpHost = reauth.SmtpHost; _smtpPort = reauth.SmtpPort; _smtpSecurity = Sec(reauth.SmtpSecurity);
            _userName = reauth.UserName;
        }
    }

    private Choice<TlsMode> Sec(TlsMode m) => SecurityChoices.FirstOrDefault(c => c.Value == m) ?? SecurityChoices[0];

    /// <summary>What kind of account the current inputs describe.</summary>
    public AccountKind EffectiveKind
    {
        get
        {
            var c = Choice?.Value ?? AccountChoice.Automatic;
            if (c == AccountChoice.Google) return AccountKind.Gmail;
            if (c == AccountChoice.Microsoft) return AccountKind.Microsoft;
            if (c == AccountChoice.Other) return AccountKind.Imap;
            var probe = new Account { Email = Email.Trim() };
            return ProviderPresets.Apply(probe) ? probe.Kind : AccountKind.Imap;
        }
    }

    public bool ShowGoogle => EffectiveKind == AccountKind.Gmail && !UseAppPassword;
    public bool ShowMicrosoft => EffectiveKind == AccountKind.Microsoft;
    public bool ShowPassword => EffectiveKind == AccountKind.Imap || (EffectiveKind == AccountKind.Gmail && UseAppPassword);
    public bool ShowAppPasswordLink => EffectiveKind == AccountKind.Gmail && !UseAppPassword;
    public string PasswordLabel => EffectiveKind == AccountKind.Gmail ? "App password (16 letters from myaccount.google.com → Security → App passwords)" : "Password";
    public bool GoogleReady => !string.IsNullOrWhiteSpace(E.Config.GoogleClientId);
    public bool MicrosoftReady => !string.IsNullOrWhiteSpace(E.Config.MicrosoftClientId);

    private void Refresh()
    {
        foreach (var p in new[] { nameof(EffectiveKind), nameof(ShowGoogle), nameof(ShowMicrosoft), nameof(ShowPassword), nameof(ShowAppPasswordLink), nameof(PasswordLabel), nameof(GoogleReady), nameof(MicrosoftReady) })
            OnPropertyChanged(p);
    }

    partial void OnEmailChanged(string value) { Error = ""; Refresh(); }
    partial void OnChoiceChanged(Choice<AccountChoice>? value) { Refresh(); _lastLookup = ""; _ = FillServersAsync(); }
    partial void OnUseAppPasswordChanged(bool value) => Refresh();

    [RelayCommand] private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;
    [RelayCommand] private void UseAppPasswordInstead() => UseAppPassword = true;

    /// <summary>Fills the server settings from the presets or autoconfig once the address is complete.</summary>
    public async Task FillServersAsync()
    {
        var email = Email.Trim();
        if (!email.Contains('@') || email.EndsWith('@') || _lastLookup == email + Choice?.Value) return;
        _lastLookup = email + Choice?.Value;
        var a = new Account { Email = email };
        if (EffectiveKind is AccountKind.Gmail or AccountKind.Microsoft) ProviderPresets.ApplyKind(a, EffectiveKind);
        else if (!ProviderPresets.Apply(a))
        {
            Status = "Looking up server settings…";
            try
            {
                if (!await ProviderPresets.LookupAsync(a, E.Http, CancellationToken.None))
                {
                    var domain = ProviderPresets.Domain(email);
                    a.ImapHost = "imap." + domain; a.ImapPort = 993; a.ImapSecurity = TlsMode.SslOnConnect;
                    a.SmtpHost = "smtp." + domain; a.SmtpPort = 465; a.SmtpSecurity = TlsMode.SslOnConnect;
                    ShowAdvanced = true;
                    Status = "Couldn't find settings automatically — please check them below.";
                }
                else Status = "";
            }
            catch { Status = ""; }
        }
        ImapHost = a.ImapHost; ImapPort = a.ImapPort; ImapSecurity = Sec(a.ImapSecurity);
        SmtpHost = a.SmtpHost; SmtpPort = a.SmtpPort; SmtpSecurity = Sec(a.SmtpSecurity);
        UserName = string.IsNullOrWhiteSpace(a.UserName) ? email : a.UserName;
    }

    private Account BuildAccount(AuthMethod auth)
    {
        var a = _reauth?.Clone() ?? new Account();
        a.Email = Email.Trim();
        a.DisplayName = DisplayName.Trim();
        a.Kind = EffectiveKind;
        a.Auth = auth;
        a.ImapHost = ImapHost.Trim(); a.ImapPort = ImapPort; a.ImapSecurity = ImapSecurity?.Value ?? TlsMode.SslOnConnect;
        a.SmtpHost = SmtpHost.Trim(); a.SmtpPort = SmtpPort; a.SmtpSecurity = SmtpSecurity?.Value ?? TlsMode.SslOnConnect;
        a.UserName = string.IsNullOrWhiteSpace(UserName) ? a.Email : UserName.Trim();
        a.ServerSavesSent = a.Kind is AccountKind.Gmail or AccountKind.Microsoft;
        if (_reauth == null)
        {
            a.SyncDays = 90;                 // design DS1: 90 days; attachments when an email is opened
            a.DownloadAttachments = false;
            a.SignatureHtml = Composer.DefaultSignatureHtml(a);
            a.SignatureDefaultApplied = true;
            var palette = new[] { "#14606E", "#4B3F86", "#B45309", "#1B6B2E", "#2F5BEA", "#B3261E" };
            a.Color = palette[E.Accounts.Count % palette.Length];
        }
        return a;
    }

    private bool ValidateEmail()
    {
        Error = "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(Email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) { Error = "Enter your full email address."; return false; }
        if (!IsReauth && E.Accounts.Any(x => x.Email.Equals(Email.Trim(), StringComparison.OrdinalIgnoreCase))) { Error = "This account is already in Magpie."; return false; }
        return true;
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    /// <summary>Password / app-password accounts: test both servers, then add.</summary>
    [RelayCommand]
    private async Task AddWithPassword()
    {
        if (!ValidateEmail()) return;
        await FillServersAsync();
        if (string.IsNullOrEmpty(Password)) { Error = "Enter the password."; return; }
        var a = BuildAccount(AuthMethod.Password);
        Busy = true;
        Status = "Checking the incoming and outgoing servers…";
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var password = Password;
            var err = await Task.Run(() => E.Connector.TestAsync(a, password, _cts.Token));
            if (err != null) { Error = err; ShowAdvanced = true; return; }
            if (IsReauth) E.UpdateAccount(a, password);
            else E.AddAccount(a, password, null);
            Done?.Invoke();
        }
        catch (OperationCanceledException) { Error = "Cancelled or timed out."; }
        catch (Exception ex) { Error = Connector.Friendly(ex); }
        finally { Busy = false; Status = ""; }
    }

    /// <summary>Sign in with Google / Microsoft in the browser (needs the client ID in Settings → Accounts).</summary>
    [RelayCommand]
    private async Task SignIn()
    {
        if (!ValidateEmail()) return;
        var kind = EffectiveKind;
        var cfg = E.OAuth.ConfigFor(kind);
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.ClientId))
        {
            Error = kind == AccountKind.Gmail
                ? "Google sign-in isn't set up yet: add your Google client ID in Settings → Accounts → Sign-in apps (or import the client JSON), or use an app password."
                : "Microsoft sign-in isn't set up yet: add your Microsoft client ID in Settings → Accounts → Sign-in apps.";
            return;
        }
        await FillServersAsync();
        Busy = true;
        Status = "Finish signing in in your browser…";
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var tokens = await E.OAuth.SignInAsync(cfg, Email.Trim(), Shell.OpenWeb, _cts.Token);
            if (!string.IsNullOrEmpty(tokens.Email) && !tokens.Email.Equals(Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Error = $"You signed in as {tokens.Email}, but this account is {Email.Trim()}. Sign in with the matching account.";
                return;
            }
            if (string.IsNullOrWhiteSpace(DisplayName) && !string.IsNullOrWhiteSpace(tokens.Name)) DisplayName = tokens.Name!;
            var a = BuildAccount(AuthMethod.OAuth2);
            Status = "Checking the mailbox…";
            E.OAuth.Remember(a.Id, tokens);
            var err = await Task.Run(() => E.Connector.TestAsync(a, null, CancellationToken.None));
            if (err != null) { Error = err; return; }
            if (IsReauth) E.UpdateAccount(a, null, tokens);
            else E.AddAccount(a, null, tokens);
            Done?.Invoke();
        }
        catch (OperationCanceledException) { Error = "Sign-in was cancelled or timed out."; }
        catch (Exception ex) { Error = ex is ReauthRequiredException or InvalidOperationException or TimeoutException ? ex.Message : Connector.Friendly(ex); }
        finally { Busy = false; Status = ""; }
    }
}
