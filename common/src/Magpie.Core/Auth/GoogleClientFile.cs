using System.Text.Json;

namespace Magpie.Core.Auth;

/// <summary>"Import Google client JSON…": the client_secret_….json Google Cloud gives for a Desktop app OAuth client.</summary>
public static class GoogleClientFile
{
    /// <summary>The client ID and secret in the file (the "installed" or "web" block, or the top level).
    /// Returns null and a plain message when it isn't such a file.</summary>
    public static (string Id, string Secret)? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var node = root.TryGetProperty("installed", out var inst) ? inst : root.TryGetProperty("web", out var web) ? web : root;
            var id = node.TryGetProperty("client_id", out var i) ? i.GetString() ?? "" : "";
            if (id.Length == 0) { error = "That file isn't a Google OAuth client file: it has no client_id."; return null; }
            var secret = node.TryGetProperty("client_secret", out var s) ? s.GetString() ?? "" : "";
            return (id, secret);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            error = "That file isn't a Google OAuth client file: " + ex.Message;
            return null;
        }
    }
}
