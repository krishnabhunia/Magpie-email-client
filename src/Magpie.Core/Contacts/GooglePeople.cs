using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magpie.Core.Models;

namespace Magpie.Core.Contacts;

/// <summary>Something the contacts can't do right now, in plain words (shown on the Contacts page).</summary>
public sealed class ContactsProblem : Exception
{
    /// <summary>The account must sign in again (to allow contacts) — the page offers "Sign in again".</summary>
    public bool NeedsSignIn { get; }
    public ContactsProblem(string message, bool needsSignIn = false) : base(message) => NeedsSignIn = needsSignIn;
}

/// <summary>
/// Google People API (design B4): the account's contacts and contact groups, and adding / changing / deleting a contact.
/// Needs the People API turned on in the Google Cloud project Magpie signs in with, and a sign-in that allows contacts.
/// </summary>
public sealed class GooglePeopleClient
{
    public const string Api = "https://people.googleapis.com/v1/";
    public const string PersonFields = "names,emailAddresses,phoneNumbers,organizations,biographies,memberships";
    private const string UpdateFields = "names,emailAddresses,phoneNumbers,organizations,biographies";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;

    public GooglePeopleClient(HttpClient http, Func<CancellationToken, Task<string>> token)
    {
        _http = http;
        _token = token;
    }

    /// <summary>Every contact of the account (with its group names).</summary>
    public async Task<List<SavedContact>> ListAsync(string accountId, CancellationToken ct)
    {
        var groups = await ListGroupsAsync(ct);
        var list = new List<SavedContact>();
        string? page = null;
        do
        {
            var root = await SendAsync(HttpMethod.Get, $"people/me/connections?personFields={PersonFields}&pageSize=1000&sortOrder=FIRST_NAME_ASCENDING"
                                                       + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, ct);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
                foreach (var p in conns.EnumerateArray()) list.Add(Parse(p, accountId, groups));
            page = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        } while (page != null);
        return list;
    }

    /// <summary>Group resource → name, the user's own groups only (contactGroupType USER_CONTACT_GROUP).</summary>
    public async Task<Dictionary<string, string>> ListGroupsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? page = null;
        do
        {
            var root = await SendAsync(HttpMethod.Get, "contactGroups?pageSize=1000" + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, ct);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("contactGroups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var g in gs.EnumerateArray())
                    if (Str(g, "groupType") == "USER_CONTACT_GROUP" && Str(g, "resourceName") is { Length: > 0 } res)
                        map[res] = Str(g, "formattedName") is { Length: > 0 } f ? f : Str(g, "name");
            page = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        } while (page != null);
        return map;
    }

    public async Task<SavedContact> CreateAsync(SavedContact c, CancellationToken ct)
    {
        var root = await SendAsync(HttpMethod.Post, $"people:createContact?personFields={PersonFields}", Body(c, withEtag: false), ct);
        return Parse(root, c.AccountId, new Dictionary<string, string>());
    }

    public async Task<SavedContact> UpdateAsync(SavedContact c, CancellationToken ct)
    {
        var root = await SendAsync(HttpMethod.Patch, $"{c.Resource}:updateContact?updatePersonFields={UpdateFields}&personFields={PersonFields}", Body(c, withEtag: true), ct);
        var saved = Parse(root, c.AccountId, new Dictionary<string, string>());
        saved.Groups = c.Groups;   // groups aren't changed here; keep the names we had
        return saved;
    }

    public Task DeleteAsync(SavedContact c, CancellationToken ct) => SendAsync(HttpMethod.Delete, $"{c.Resource}:deleteContact", null, ct);

    // ───────────────────────── JSON ─────────────────────────

    internal static JsonObject Body(SavedContact c, bool withEtag)
    {
        var o = new JsonObject
        {
            ["names"] = new JsonArray(new JsonObject
            {
                ["givenName"] = c.GivenName, ["familyName"] = c.FamilyName,
                ["unstructuredName"] = c.Name.Length > 0 ? c.Name : (c.GivenName + " " + c.FamilyName).Trim(),
            }),
            ["emailAddresses"] = new JsonArray(c.Emails.Where(e => e.Trim().Length > 0).Select(e => (JsonNode)new JsonObject { ["value"] = e.Trim() }).ToArray()),
            ["phoneNumbers"] = new JsonArray(c.Phones.Where(p => p.Trim().Length > 0).Select(p => (JsonNode)new JsonObject { ["value"] = p.Trim() }).ToArray()),
            ["organizations"] = c.Company.Length > 0 || c.Title.Length > 0
                ? new JsonArray(new JsonObject { ["name"] = c.Company, ["title"] = c.Title }) : new JsonArray(),
            ["biographies"] = c.Notes.Length > 0 ? new JsonArray(new JsonObject { ["value"] = c.Notes, ["contentType"] = "TEXT_PLAIN" }) : new JsonArray(),
        };
        if (withEtag) o["etag"] = c.Etag;
        return o;
    }

    internal static SavedContact Parse(JsonElement p, string accountId, IReadOnlyDictionary<string, string> groups)
    {
        var c = new SavedContact { AccountId = accountId, Resource = Str(p, "resourceName"), Etag = Str(p, "etag"), Updated = DateTimeOffset.Now };
        if (First(p, "names") is { } name)
        {
            c.GivenName = Str(name, "givenName");
            c.FamilyName = Str(name, "familyName");
            c.Name = Str(name, "displayName") is { Length: > 0 } d ? d : Str(name, "unstructuredName");
        }
        c.Emails = All(p, "emailAddresses").Select(e => Str(e, "value")).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        c.Phones = All(p, "phoneNumbers").Select(e => Str(e, "value")).Where(v => v.Length > 0).Distinct().ToList();
        if (First(p, "organizations") is { } org) { c.Company = Str(org, "name"); c.Title = Str(org, "title"); }
        if (First(p, "biographies") is { } bio) c.Notes = Str(bio, "value");
        c.Groups = All(p, "memberships")
            .Select(m => m.TryGetProperty("contactGroupMembership", out var g) ? Str(g, "contactGroupResourceName") : "")
            .Where(groups.ContainsKey).Select(r => groups[r]).Distinct().ToList();
        return c;
    }

    private static JsonElement? First(JsonElement e, string name) => All(e, name).Cast<JsonElement?>().FirstOrDefault();

    private static IEnumerable<JsonElement> All(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ───────────────────────── HTTP ─────────────────────────

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        throw Problem(resp.StatusCode, text);
    }

    /// <summary>Google's error in plain words: the API turned off, the sign-in not allowing contacts, or the rest.</summary>
    internal static Exception Problem(HttpStatusCode status, string body)
    {
        var reason = "";
        var message = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var err = doc.RootElement.GetProperty("error");
            message = Str(err, "message");
            if (err.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Array)
                foreach (var d in det.EnumerateArray()) if (Str(d, "reason") is { Length: > 0 } r) { reason = r; break; }
            if (reason.Length == 0 && err.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0) reason = Str(errs[0], "reason");
        }
        catch (Exception) { }
        if (reason is "accessNotConfigured" or "SERVICE_DISABLED")
            return new ContactsProblem("The Google People API is off in the Google Cloud project Magpie signs in with. Turn it on there (APIs & Services → Library → Google People API), then wait a few minutes.");
        if (status == HttpStatusCode.Unauthorized || reason is "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT" || message.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase))
            return new ContactsProblem("Sign in to this Google account again to show its contacts here.", needsSignIn: true);
        return new HttpRequestException($"Google Contacts: {(message.Length > 0 ? message : status.ToString())}", null, status);
    }
}
