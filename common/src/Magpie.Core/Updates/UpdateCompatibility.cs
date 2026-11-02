using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Updates;

/// <summary>A release's published data and installation contract, bound to the exact downloadable files.</summary>
public sealed class ReleaseCompatibility
{
    public const string AssetName = "Magpie.update.json";
    public int FormatVersion { get; init; }
    public string Version { get; init; } = "";
    public int DatabaseSchemaMin { get; init; }
    public int DatabaseSchemaMax { get; init; }
    public int SettingsSchemaMin { get; init; }
    public int SettingsSchemaMax { get; init; }
    public string[] InstallKinds { get; init; } = [];
    public Dictionary<string, string> AssetSha256 { get; init; } = new(StringComparer.Ordinal);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ReleaseCompatibility Parse(string json, AppVersion expected)
    {
        var manifest = JsonSerializer.Deserialize<ReleaseCompatibility>(json, Json)
                       ?? throw new InvalidDataException("The release's compatibility information is empty.");
        if (manifest.FormatVersion != 1 || manifest.Version != expected.ToString()
            || manifest.DatabaseSchemaMin < 0 || manifest.DatabaseSchemaMax < manifest.DatabaseSchemaMin
            || manifest.SettingsSchemaMin < 1 || manifest.SettingsSchemaMax < manifest.SettingsSchemaMin
            || manifest.InstallKinds == null || manifest.InstallKinds.Length == 0
            || manifest.InstallKinds.Any(k => !UpdateInstallKinds.All.Contains(k, StringComparer.Ordinal))
            || manifest.AssetSha256 == null || manifest.AssetSha256.Count == 0
            || manifest.AssetSha256.Any(p => string.IsNullOrWhiteSpace(p.Key)
                || Path.GetFileName(p.Key) != p.Key || p.Key.Contains('/') || p.Key.Contains('\\')
                || !Regex.IsMatch(p.Value ?? "", "^[0-9a-fA-F]{64}$")))
            throw new InvalidDataException("The release's compatibility information is invalid or belongs to a different version.");
        return manifest;
    }
}

public static class UpdateInstallKinds
{
    public const string WindowsPortable = "windows-portable";
    public const string WindowsPerUser = "windows-per-user";
    public const string WindowsAllUsers = "windows-all-users";
    public const string MacApp = "mac-app";
    public static IReadOnlyList<string> All { get; } = [WindowsPortable, WindowsPerUser, WindowsAllUsers, MacApp];
}

public sealed record UpdateProfile(int DatabaseSchema, int SettingsSchema);
public sealed record CompatibilityResult(bool CanInstall, string Reason);
public sealed record VersionChoice(ReleaseInfo Release, bool Prerelease, CompatibilityResult Compatibility);

public static class UpdateCompatibility
{
    /// <summary>Inspects existing files without opening MailStore, whose constructor migrates the database.</summary>
    public static UpdateProfile ReadProfile(AppPaths paths)
    {
        var database = MailStore.SchemaVersion;
        if (File.Exists(paths.Database))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = paths.Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            database = Convert.ToInt32(command.ExecuteScalar());
        }
        var settings = 1;
        if (File.Exists(paths.Settings))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(paths.Settings));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Magpie's settings file cannot be checked safely.");
            // The persisted model has always used PascalCase. Also accept a manifest/test-style camelCase key.
            if (document.RootElement.TryGetProperty("SchemaVersion", out var schema)
                || document.RootElement.TryGetProperty("schemaVersion", out schema)) settings = schema.GetInt32();
        }
        return new(database, settings);
    }

    public static CompatibilityResult Evaluate(ReleaseInfo release, ReleaseCompatibility? manifest,
        UpdateProfile profile, string installKind)
    {
        if (!release.AssetAvailable) return new(false, "The application or its checksum is missing from this release.");
        if (manifest == null) return new(false, "This release has no verified compatibility information. Keep your current version.");
        if (manifest.FormatVersion != 1 || manifest.Version != release.Version.ToString())
            return new(false, "The compatibility information does not match this version.");
        if (!manifest.InstallKinds.Contains(installKind, StringComparer.Ordinal))
            return new(false, "This release does not support changing this installation safely. Keep this copy until a compatible installer is available.");
        if (profile.DatabaseSchema < manifest.DatabaseSchemaMin || profile.DatabaseSchema > manifest.DatabaseSchemaMax)
            return new(false, "This version cannot safely read your current mail database. Your mail and drafts have not been changed.");
        if (profile.SettingsSchema < manifest.SettingsSchemaMin || profile.SettingsSchema > manifest.SettingsSchemaMax)
            return new(false, "This version cannot safely read your current settings. Your accounts and settings have not been changed.");
        if (!manifest.AssetSha256.TryGetValue(release.AssetName, out var hash)
            || !Regex.IsMatch(hash ?? "", "^[0-9a-fA-F]{64}$"))
            return new(false, "The compatibility information does not include a verified file for this platform.");
        return new(true, "Compatible with your mail, settings and installation. SHA-256 is checked before installation.");
    }
}
