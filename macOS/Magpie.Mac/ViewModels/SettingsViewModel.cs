using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.Core;
using Magpie.Core.Auth;
using Magpie.Core.Models;
using Magpie.Mac.Services;

namespace Magpie.Mac.ViewModels;

public sealed class AccountRow
{
    public Account Account { get; init; } = new();
    public string Email => Account.Email;
    public string Kind => Account.Kind switch
    {
        AccountKind.Gmail => Account.Auth == AuthMethod.OAuth2 ? "Google · signed in" : "Google · app password",
        AccountKind.Microsoft => "Microsoft · signed in",
        _ => $"IMAP · {Account.ImapHost}",
    };
}

/// <summary>Settings for Magpie for Mac (first version): Accounts (+ sign-in apps), Updates, About.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static MailEngine E => AppServices.Engine;

    public ObservableCollection<AccountRow> Accounts { get; } = new();
    public MacUpdateService? Updates => AppServices.Updates;

    [ObservableProperty] private string _googleClientId = "";
    [ObservableProperty] private string _googleClientSecret = "";
    [ObservableProperty] private string _microsoftClientId = "";
    [ObservableProperty] private string _savedNote = "";

    public string VersionLine => $"Magpie {AppServices.Current}" + (AppServices.ReleaseDate.Length > 0 ? $" · released {AppServices.ReleaseDate}" : "");
    public string DataFolder => E.Paths.Root;
    public string DataNote => $"Your mail, settings and sign-ins are kept on this Mac in {E.Paths.Root} (passwords and sign-ins encrypted with a key in your Keychain). Caches and update downloads are in {E.Paths.LocalRoot}.";

    public SettingsViewModel()
    {
        var c = E.Config;
        _googleClientId = c.GoogleClientId;
        _googleClientSecret = c.GoogleClientSecret;
        _microsoftClientId = c.MicrosoftClientId;
        LoadAccounts();
        E.Settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged() => AppServices.Post(LoadAccounts);

    public void Detach() => E.Settings.Changed -= OnSettingsChanged;

    private void LoadAccounts()
    {
        Accounts.Clear();
        foreach (var a in E.Accounts) Accounts.Add(new AccountRow { Account = a });
    }

    [RelayCommand] private void AddAccount() => Views.AddAccountWindow.Open(null);
    [RelayCommand] private void SignInAgain(AccountRow? row) { if (row != null) Views.AddAccountWindow.Open(row.Account); }

    [RelayCommand]
    private async Task RemoveAccount(AccountRow? row)
    {
        if (row == null) return;
        if (!await Dialogs.Confirm("Remove account", $"Remove {row.Email} from Magpie? Its emails are deleted from this Mac (not from the server).", "Remove")) return;
        E.RemoveAccount(row.Account.Id);
        LoadAccounts();
    }

    /// <summary>Saves the sign-in apps (client IDs).</summary>
    [RelayCommand]
    private void SaveSignInApps()
    {
        var c = E.Config;
        c.GoogleClientId = GoogleClientId.Trim();
        c.GoogleClientSecret = GoogleClientSecret.Trim();
        c.MicrosoftClientId = MicrosoftClientId.Trim();
        E.Settings.Save();
        SavedNote = "Saved " + DateTime.Now.ToString("HH:mm");
    }

    /// <summary>"Import Google client JSON…": fills the Google ID and secret from the file Google Cloud gave.</summary>
    public string? ImportGoogleJson(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { return "Couldn't read that file: " + ex.Message; }
        if (GoogleClientFile.Parse(json, out var error) is not { } parsed) return error;
        GoogleClientId = parsed.Id;
        GoogleClientSecret = parsed.Secret;
        SaveSignInApps();
        return null;
    }

    [RelayCommand] private void OpenDataFolder() => Shell.Open(E.Paths.Root);
    [RelayCommand] private void OpenLog() { if (Log.FilePath != null) Shell.Reveal(Log.FilePath); }
    [RelayCommand] private void OpenReleases() => Shell.Open(Magpie.Core.Updates.UpdateClient.ReleasesPage);
}
