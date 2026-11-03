using System.Text.Json;

namespace Magpie.Core.Settings;

/// <summary>Design SP1: when the start-up screen (splash) shows. Settings → General → Start-up screen.</summary>
public enum SplashMode { Always = 0, AfterUpdate = 1, Never = 2 }

/// <summary>One account chip on the start-up screen (design SP1-B).</summary>
public sealed record SplashAccount(string Email, string Color);

/// <summary>
/// What the start-up screen needs before Magpie has loaded anything: read straight from settings.json with no
/// side effects (no .bad copy, no Normalise, no secrets), so it can run in the first milliseconds of a start.
/// A missing or unreadable file gives the defaults: the screen shows, in the Windows theme, without chips.
/// </summary>
public sealed class SplashPrefs
{
    public SplashMode Mode { get; init; } = SplashMode.Always;
    public bool ShowAccounts { get; init; } = true;
    public ThemeMode Theme { get; init; } = ThemeMode.MatchWindows;
    public IReadOnlyList<SplashAccount> Accounts { get; init; } = Array.Empty<SplashAccount>();
    /// <summary>The main window's saved place (device-independent pixels), so the screen appears where Magpie will open.</summary>
    public double WindowLeft { get; init; } = double.NaN;
    public double WindowTop { get; init; } = double.NaN;
    public double WindowWidth { get; init; } = double.NaN;
    public double WindowHeight { get; init; } = double.NaN;

    public static SplashPrefs Read(string settingsPath)
    {
        try { return File.Exists(settingsPath) ? FromJson(File.ReadAllText(settingsPath)) : new SplashPrefs(); }
        catch { return new SplashPrefs(); }
    }

    public static SplashPrefs FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new SplashPrefs();
            var ap = Get(root, "Appearance");
            var win = Get(root, "Window");
            var accounts = new List<SplashAccount>();
            if (Get(root, "Accounts") is { ValueKind: JsonValueKind.Array } list)
                foreach (var a in list.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var email = Str(Get(a, "Email")).Trim();
                    if (email.Length == 0) continue;
                    var color = Str(Get(a, "Color")).Trim();
                    accounts.Add(new SplashAccount(email, IsHexColour(color) ? color : "#14606E"));
                }
            return new SplashPrefs
            {
                Mode = Enum<SplashMode>(ap is { } a1 ? Get(a1, "Splash") : null, SplashMode.Always),
                ShowAccounts = ap is { } a2 && Get(a2, "SplashAccounts") is { ValueKind: JsonValueKind.False } ? false : true,
                Theme = Enum<ThemeMode>(ap is { } a3 ? Get(a3, "Theme") : null, ThemeMode.MatchWindows),
                Accounts = accounts,
                WindowLeft = Num(win, "Left"), WindowTop = Num(win, "Top"), WindowWidth = Num(win, "Width"), WindowHeight = Num(win, "Height"),
            };
        }
        catch { return new SplashPrefs(); }
    }

    private static JsonElement? Get(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";

    private static double Num(JsonElement? obj, string name)
    {
        if (obj is not { } o || Get(o, name) is not { } v) return double.NaN;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return double.NaN;   // "NaN" strings and anything else
    }

    private static T Enum<T>(JsonElement? e, T fallback) where T : struct, System.Enum
    {
        if (e is { ValueKind: JsonValueKind.String } s && System.Enum.TryParse<T>(s.GetString(), true, out var v) && System.Enum.IsDefined(v)) return v;
        if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i) && System.Enum.IsDefined(typeof(T), i)) return (T)(object)i;
        return fallback;
    }

    private static bool IsHexColour(string c) =>
        c.Length is 7 or 9 && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>Design SP1 rules: when the screen shows, how long it stays, and its wording.</summary>
public static class SplashPlan
{
    /// <summary>Shortest time on screen, so it never just flickers.</summary>
    public static readonly TimeSpan MinVisible = TimeSpan.FromMilliseconds(800);
    /// <summary>After this, "This is taking longer than usual" and Open log folder appear.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(10);
    public const int StepCount = 4;

    /// <summary>
    /// Start with Windows (<c>--tray</c>) is quiet: never. A second launch never gets this far. Otherwise the setting
    /// decides; "Only after an update" shows on the first start of a new version.
    /// </summary>
    public static bool ShouldShow(SplashMode mode, bool afterUpdate, bool startInTray) =>
        !startInTray && mode switch
        {
            SplashMode.Always => true,
            SplashMode.AfterUpdate => afterUpdate,
            _ => false,
        };

    /// <summary>How long to keep the finished screen up so it was visible for at least <see cref="MinVisible"/>.</summary>
    public static TimeSpan HoldBeforeClose(TimeSpan shownFor) =>
        shownFor >= MinVisible ? TimeSpan.Zero : MinVisible - shownFor;

    /// <summary>File in the settings folder holding the last version that started all the way (its main window drew).</summary>
    public const string LastStartedFile = "last-started-version.txt";

    /// <summary>
    /// "Only the first time after an update" must also catch updates made with the installer or by replacing the
    /// portable EXE, which pass no --after-update: a version different from the last one that started is new. With no
    /// record yet, an existing settings file means Magpie was used before this version (the first start of 14.0.0
    /// after 13.x), and no settings file means a fresh install, which is not an update.
    /// </summary>
    public static bool IsNewVersion(string? lastStarted, string current, bool updaterSaysSo, bool hasSettings)
    {
        if (updaterSaysSo) return true;
        var last = (lastStarted ?? "").Trim();
        if (last.Length == 0) return hasSettings;
        return !string.Equals(last, current.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string? ReadLastStarted(string settingsFolder)
    {
        try
        {
            var f = Path.Combine(settingsFolder, LastStartedFile);
            if (!File.Exists(f)) return null;
            var text = File.ReadAllText(f).Trim();
            return text.Length is > 0 and <= 64 ? text : null;
        }
        catch { return null; }
    }

    /// <summary>Records <paramref name="version"/> once it has started all the way; never throws.</summary>
    public static void RememberStarted(string settingsFolder, string version)
    {
        try
        {
            if (string.Equals(ReadLastStarted(settingsFolder), version, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(settingsFolder);
            var f = Path.Combine(settingsFolder, LastStartedFile);
            File.WriteAllText(f + ".next", version);
            File.Move(f + ".next", f, overwrite: true);
        }
        catch (Exception ex) { Log.Warn("could not note the started version: " + ex.Message); }
    }

    public static bool IsSlow(TimeSpan shownFor, bool finished) => !finished && shownFor >= SlowAfter;

    /// <summary>The step lines: (while it runs, once it's done). Step 3 names the accounts.</summary>
    public static (string Now, string Done) Step(int index, int accounts) => index switch
    {
        0 => ("Checking the last update", "Checked the last update"),
        1 => ("Opening your mail folder", "Opened your mail folder"),
        2 => ("Loading settings, look and fonts", "Loaded settings, look and fonts"),
        3 => accounts switch
        {
            0 => ("Opening Magpie", "Opened Magpie"),
            1 => ("Starting 1 account", "Started 1 account"),
            _ => ($"Starting {accounts} accounts", $"Started {accounts} accounts"),
        },
        _ => ("", ""),
    };

    /// <summary>"42 ms", "1.4 s", "12 s".</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalMilliseconds < 1000 ? $"{Math.Max(0, (int)Math.Round(t.TotalMilliseconds))} ms"
        : t.TotalSeconds < 10 ? (Math.Floor(t.TotalSeconds * 10) / 10).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"
        : $"{(int)t.TotalSeconds} s";

    /// <summary>A short log line: "start-up 1.2 s (update 42 ms · mail folder 318 ms · look 121 ms · accounts 264 ms)".</summary>
    public static string LogLine(IReadOnlyList<TimeSpan?> steps, TimeSpan total)
    {
        string[] names = { "update", "mail folder", "look", "accounts" };
        var parts = steps.Select((s, i) => s is { } t && i < names.Length ? names[i] + " " + Duration(t) : null).Where(p => p != null);
        return "start-up " + Duration(total) + " (" + string.Join(" · ", parts) + ")";
    }
}
