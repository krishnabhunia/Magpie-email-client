using Magpie.Core.Settings;

namespace Magpie.Core.Mail;

/// <summary>
/// Design OW1 (option A): an email opened in its own window — every word of it, which window an email belongs to,
/// the step to the previous / next email of the list it was opened from, and where the window is put. The screens
/// only place these.
/// </summary>
public static class EmailWindow
{
    public const string OpenWords = "Open in new window";
    public const string OpenKey = "Enter";
    public const string ButtonName = "New window";
    public const string ButtonTip = "Open this email in its own window (double-click it in the list, or press Enter)";
    public const string PreviousTip = "Previous email in the list (K)";
    public const string NextTip = "Next email in the list (J)";

    /// <summary>The window's caption: the subject, then the app.</summary>
    public static string Title(string? subject) => (string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject.Trim()) + " — Magpie";

    /// <summary>One window per conversation: this names it (account and conversation, as the list does).</summary>
    public static string Key(string accountId, string threadKey) => accountId + "\n" + threadKey;

    /// <summary>
    /// Where the email is in the list the window was opened from: "2 of 42 in Inbox". "" when the email is not in
    /// that list (opened from Contacts, or the list has moved on) — then there is no previous or next either.
    /// </summary>
    public static string Position(IReadOnlyList<string> keys, string current, string? listName)
    {
        var at = IndexOf(keys, current);
        if (at < 0) return "";
        var where = string.IsNullOrWhiteSpace(listName) ? "" : " in " + listName.Trim();
        return $"{at + 1:N0} of {keys.Count:N0}{where}";
    }

    /// <summary>The email one step up (-1) or down (+1) in that list; null at either end or when it is not in the list.</summary>
    public static string? Step(IReadOnlyList<string> keys, string current, int delta)
    {
        var at = IndexOf(keys, current);
        if (at < 0) return null;
        var to = at + Math.Sign(delta);
        return to >= 0 && to < keys.Count ? keys[to] : null;
    }

    private static int IndexOf(IReadOnlyList<string> keys, string current)
    {
        for (var i = 0; i < keys.Count; i++)
            if (string.Equals(keys[i], current, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>A screen area in the units the window uses (left, top, width, height).</summary>
    public readonly record struct Area(double Left, double Top, double Width, double Height);

    /// <summary>How far each further window is shifted down and right, so the windows do not hide each other.</summary>
    public const double CascadeStep = 28;

    /// <summary>
    /// Where a new email window goes. Its size is the saved one, never larger than the screen. Its place is the saved
    /// one while that is still on a screen; otherwise a little inside the main window; otherwise the screen's centre.
    /// Each window already open shifts it by <see cref="CascadeStep"/>, wrapping back when it would leave the screen.
    /// </summary>
    public static Area Place(EmailWindowPlacement saved, Area screen, Area? mainWindow, int alreadyOpen)
    {
        var width = Math.Min(Math.Max(saved.Width, EmailWindowPlacement.MinWidth), Math.Max(screen.Width, EmailWindowPlacement.MinWidth));
        var height = Math.Min(Math.Max(saved.Height, EmailWindowPlacement.MinHeight), Math.Max(screen.Height, EmailWindowPlacement.MinHeight));
        double left, top;
        if (saved.HasSavedPlacement && OnScreen(saved.Left, saved.Top, screen)) (left, top) = (saved.Left, saved.Top);
        else if (mainWindow is { } main && OnScreen(main.Left + 80, main.Top + 60, screen)) (left, top) = (main.Left + 80, main.Top + 60);
        else (left, top) = (screen.Left + Math.Max(0, (screen.Width - width) / 2), screen.Top + Math.Max(0, (screen.Height - height) / 2));

        var steps = Math.Max(0, alreadyOpen);
        if (steps > 0)
        {
            // As many steps as fit before the window's title bar would leave the screen, then start again.
            var room = Math.Min(screen.Left + screen.Width - 100 - left, screen.Top + screen.Height - 100 - top);
            var fit = Math.Max(1, (int)Math.Floor(room / CascadeStep) + 1);
            var shift = steps % fit * CascadeStep;
            left += shift;
            top += shift;
        }
        return new Area(left, top, width, height);
    }

    /// <summary>A window's top-left corner is usable when enough of its title bar is on the screen to grab.</summary>
    public static bool OnScreen(double left, double top, Area screen) =>
        double.IsFinite(left) && double.IsFinite(top)
        && left > screen.Left - 50 && top > screen.Top - 50
        && left < screen.Left + screen.Width - 100 && top < screen.Top + screen.Height - 100;
}
