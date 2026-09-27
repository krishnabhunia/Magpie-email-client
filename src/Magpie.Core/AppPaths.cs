namespace Magpie.Core;

/// <summary>Where Magpie keeps its data. Everything is local: %APPDATA%\Magpie by default.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MimeCache);
    }

    public static AppPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Magpie"));

    public string Database => Path.Combine(Root, "mail.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Secrets => Path.Combine(Root, "secrets.json");
    public string MimeCache => Path.Combine(Root, "messages");
    public string Logs => Root;

    public string MimePath(string accountId, long messageRowId) =>
        Path.Combine(MimeCache, Sanitize(accountId), messageRowId + ".eml");

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
