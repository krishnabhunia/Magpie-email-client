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
    /// Where a new email window goes. <paramref name="screens"/> are the monitors' areas, the main one first. Its place
    /// is the saved one while that is still on one of them; otherwise a little inside the main window; otherwise the
    /// middle of the main monitor. Its size is the saved one, never larger than the monitor it opens on. Each window
    /// already open shifts it by <see cref="CascadeStep"/>, starting again before it would leave that monitor.
    /// The monitors are checked one by one: with monitors of different sizes, or set at different heights, the
    /// rectangle around all of them has corners that are on no monitor, and a window there cannot be reached.
    /// </summary>
    public static Area Place(EmailWindowPlacement saved, IReadOnlyList<Area> screens, Area? mainWindow, int alreadyOpen)
    {
        var usable = screens.Where(s => s.Width > 0 && s.Height > 0).ToList();
        if (usable.Count == 0) usable.Add(new Area(0, 0, EmailWindowPlacement.DefaultWidth, EmailWindowPlacement.DefaultHeight));
        Area home;
        double left, top;
        var centred = false;
        if (saved.HasSavedPlacement && ScreenOf(saved.Left, saved.Top, usable) is { } kept) (home, left, top) = (kept, saved.Left, saved.Top);
        else if (mainWindow is { } main && ScreenOf(main.Left + 80, main.Top + 60, usable) is { } near) (home, left, top) = (near, main.Left + 80, main.Top + 60);
        else (home, left, top, centred) = (usable[0], 0, 0, true);

        var width = Math.Min(Math.Max(saved.Width, EmailWindowPlacement.MinWidth), Math.Max(home.Width, EmailWindowPlacement.MinWidth));
        var height = Math.Min(Math.Max(saved.Height, EmailWindowPlacement.MinHeight), Math.Max(home.Height, EmailWindowPlacement.MinHeight));
        if (centred) (left, top) = (home.Left + Math.Max(0, (home.Width - width) / 2), home.Top + Math.Max(0, (home.Height - height) / 2));

        var steps = Math.Max(0, alreadyOpen);
        if (steps > 0)
        {
            // As many steps as fit before the window's title bar would leave its monitor, then start again.
            var room = Math.Min(home.Left + home.Width - 100 - left, home.Top + home.Height - 100 - top);
            var fit = Math.Max(1, (int)Math.Ceiling(room / CascadeStep));
            var shift = steps % fit * CascadeStep;
            left += shift;
            top += shift;
        }
        return new Area(left, top, width, height);
    }

    /// <summary>The same for one screen area.</summary>
    public static Area Place(EmailWindowPlacement saved, Area screen, Area? mainWindow, int alreadyOpen) =>
        Place(saved, new[] { screen }, mainWindow, alreadyOpen);

    /// <summary>The monitor a window with this top-left corner can be reached on; null when it is on none.</summary>
    public static Area? ScreenOf(double left, double top, IReadOnlyList<Area> screens)
    {
        foreach (var s in screens)
            if (OnScreen(left, top, s)) return s;
        return null;
    }

    /// <summary>A window's top-left corner is usable when enough of its title bar is on the screen to grab.</summary>
    public static bool OnScreen(double left, double top, Area screen) =>
        double.IsFinite(left) && double.IsFinite(top)
        && left > screen.Left - 50 && top > screen.Top - 50
        && left < screen.Left + screen.Width - 100 && top < screen.Top + screen.Height - 100;
}
