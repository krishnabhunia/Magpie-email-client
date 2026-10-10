namespace Magpie.App.ViewModels;

/// <summary>"Download emails from the last …" (design DS1). Days 0 = everything.</summary>
public sealed record DownloadChoice(string Label, int Days)
{
    public static readonly DownloadChoice[] Standard =
    {
        new("30 days", 30), new("90 days", 90), new("6 months", 182), new("1 year", 365), new("Everything", 0),
    };

    /// <summary>The standard choices, plus <paramref name="current"/> when it is none of them (an older setting).</summary>
    public static DownloadChoice[] For(int current) =>
        Standard.Any(c => c.Days == current) ? Standard
            : Standard.Append(new DownloadChoice(current + " days", current)).OrderBy(c => c.Days == 0 ? int.MaxValue : c.Days).ToArray();

    public static int Normalise(int days) => days <= 0 ? 0 : Math.Clamp(days, 7, 3650);
}
