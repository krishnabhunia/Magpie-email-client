using System.Reflection;

namespace Magpie.Core.Auth;

/// <summary>
/// Design SY1 / SA1 (approved 30 Oct 2026): Magpie's own Google and Microsoft sign-in apps, built into each release from
/// the CI secrets MAGPIE_GOOGLE_CLIENT_ID / MAGPIE_GOOGLE_CLIENT_SECRET / MAGPIE_MICROSOFT_CLIENT_ID (AssemblyMetadata in
/// Magpie.Core.csproj). On a brand-new PC, signing in to the home account then needs nothing typed first. The ones set in
/// Settings → Accounts → Sign-in apps win when present. A Desktop app's client secret isn't secret (Google says so):
/// it ships inside every copy.
/// </summary>
public static class BuiltInSignIn
{
    private static string Meta(string key) =>
        typeof(BuiltInSignIn).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";

    public static string GoogleClientId { get; } = Meta("MagpieGoogleClientId");
    public static string GoogleClientSecret { get; } = Meta("MagpieGoogleClientSecret");
    public static string MicrosoftClientId { get; } = Meta("MagpieMicrosoftClientId");

    /// <summary>The Google app to sign in with: the one set in Settings, else the built-in one.</summary>
    public static (string Id, string Secret) Google(string settingsId, string settingsSecret) =>
        !string.IsNullOrWhiteSpace(settingsId) ? (settingsId.Trim(), (settingsSecret ?? "").Trim()) : (GoogleClientId, GoogleClientSecret);

    public static string Microsoft(string settingsId) => !string.IsNullOrWhiteSpace(settingsId) ? settingsId.Trim() : MicrosoftClientId;
}
