using System.Windows;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Auth;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.App.Services;

/// <summary>
/// Design SY1 on Windows: connects the accounts copy service to the app (settings changes on the UI thread, the adding
/// window, the questions), and the new-PC step of Add account.
/// </summary>
public static class AccountsCopyUi
{
    private static readonly HashSet<string> Asked = new(StringComparer.OrdinalIgnoreCase);
    private static bool _passwordAsked, _availableShown;

    public static void Attach(MailEngine e, string appVersion)
    {
        var svc = e.AccountsCopy;
        svc.AppVersion = appVersion;
        svc.RunOnUi = a => Ui.Dispatcher.InvokeAsync(a).Task;
        // An open Settings window would write its older values back over what the copy brings: wait until it closes.
        svc.Paused = () => Ui.Dispatcher.Invoke(() => Application.Current.Windows.OfType<SettingsWindow>().Any());
        svc.StoppedElsewhere += pc => Ui.Post(() =>
            AppServices.Tray?.ShowBalloon("Accounts copy stopped", $"Magpie on {(pc.Length > 0 ? pc : "another PC")} stopped the accounts copy. Your accounts here stay as they are.", () => ShowMain()));
        svc.AccountsAdded += added => Ui.Post(() =>
        {
            AddingAccountsWindow.ShowFor(added);
            AppServices.Tray?.ShowBalloon("Accounts added", added.Count == 1 ? $"{added[0].Email} came from your other PC." : $"{added.Count} accounts came from your other PCs.", () => ShowMain());
        });
        // Asked once per session each; the next start asks again if still open.
        svc.RemovedElsewhere += (account, removed) => Ui.Post(() =>
        {
            if (!Asked.Add(account.Email)) return;
            var by = removed.ByPc.Length > 0 ? removed.ByPc : "another PC";
            var choice = ChoiceDialog.Ask(ShowMain(), "Removed on another PC",
                $"{account.Email} was removed from Magpie on {by} ({removed.When.ToLocalTime():d MMM, HH:mm}). Remove it here too?",
                "Remove it here too", "Keep it");
            if (choice == 0) svc.AnswerRemoved(account, removeHere: true);
            else if (choice == 1) svc.AnswerRemoved(account, removeHere: false);
            else Asked.Remove(account.Email);
        });
        svc.PasswordNeeded += () => Ui.Post(() =>
        {
            if (_passwordAsked) return;
            _passwordAsked = true;
            AppServices.Tray?.ShowBalloon("Type the sync password", "Your accounts copy has a new sync password. Click to type it once on this PC.", () => AskPassword(ShowMain()));
        });
        svc.AccountsAvailable += list => Ui.Post(() =>
        {
            if (_availableShown || list.Count == 0) return;
            _availableShown = true;
            AppServices.Tray?.ShowBalloon("Accounts from your other PCs",
                (list.Count == 1 ? $"{list[0].Account.Email} is" : $"{list.Count} accounts are") + " waiting in Settings → Accounts.", () => ShowMain());
        });
    }

    private static Window? ShowMain() => (Application.Current as App)?.ShowMain() ?? Application.Current.MainWindow;

    public static void AskPassword(Window? owner)
    {
        var p = PasswordDialog.Ask(owner, "Sync password",
            "Type the sync password of your accounts copy (it was chosen or changed on one of your PCs).", confirm: false, AccountsCopy.MinPasswordLength, "OK");
        if (p != null) { _passwordAsked = false; AppServices.Engine.AccountsCopy.SetPassword(p); }
    }

    /// <summary>
    /// Design SY1·3, the new-PC step of Add account: after signing in (and the servers checked), look in the mailbox for
    /// a copy. When there is one and the user opens it, the account and the ticked ones are added and true is returned;
    /// false = add only this account as usual (no copy, "Not now", or the copy couldn't be read).
    /// </summary>
    public static async Task<bool> OfferAsync(Window? owner, Account account, string? password, OAuthTokens? tokens, CancellationToken ct)
    {
        var e = AppServices.Engine;
        if (e.Config.AccountsCopy.Enabled) return false;   // this PC keeps its own copy already
        byte[]? file;
        // Its own time limit: a slow server means "no copy found" (the account is added as usual), not a failed sign-in.
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookup.CancelAfter(TimeSpan.FromSeconds(45));
        try { file = await e.AccountsCopy.FindAsync(account, password, lookup.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Log.Warn("accounts copy: looking for a copy took too long"); return false; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("accounts copy: looking for a copy failed: " + ex.Message); return false; }
        ct.ThrowIfCancellationRequested();   // the window was closed meanwhile: add nothing
        if (file == null || AccountsCopy.Peek(file) is not { Accounts: > 0 }) return false;   // none, or only a "stopped" / "moved" note
        var choice = AccountsFoundDialog.Ask(owner, file, account.Email, e.Accounts.Select(a => a.Email).ToList());
        if (choice == null) return false;
        e.AddAccount(account, password, tokens);
        var added = e.AccountsCopy.AddFromCopy(choice.Copy, account, choice.Password, choice.Emails);
        if (added.Count > 0) AddingAccountsWindow.ShowFor(new[] { account }.Concat(added).ToList());
        return true;
    }
}
