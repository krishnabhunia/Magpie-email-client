using System.Net;
using System.Text;
using System.Text.Json;
using Magpie.Core.Auth;
using Magpie.Core.Contacts;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>8.0.0: Contacts (design B4, queue #9, issue #5): Google People API, contacts on this PC, suggestions.</summary>
public class Release800Tests
{
    private const string Anita = """
        {"resourceName":"people/c1","etag":"e1",
         "names":[{"displayName":"Anita Rao","givenName":"Anita","familyName":"Rao"}],
         "emailAddresses":[{"value":"anita@vendorco.in"},{"value":"ANITA@vendorco.in"},{"value":"anita.rao@gmail.com"}],
         "phoneNumbers":[{"value":"+91 98450 12345"}],
         "organizations":[{"name":"VendorCo","title":"Product manager"}],
         "biographies":[{"value":"Met at the Bengaluru expo"}],
         "memberships":[{"contactGroupMembership":{"contactGroupResourceName":"contactGroups/work1"}},
                        {"contactGroupMembership":{"contactGroupResourceName":"contactGroups/myContacts"}}]}
        """;

    [Fact]
    public void A_Google_person_becomes_a_saved_contact()
    {
        using var doc = JsonDocument.Parse(Anita);
        var c = GooglePeopleClient.Parse(doc.RootElement, "A", new Dictionary<string, string> { ["contactGroups/work1"] = "Work" });
        Assert.Equal(("people/c1", "e1", "Anita Rao", "Anita", "Rao"), (c.Resource, c.Etag, c.Name, c.GivenName, c.FamilyName));
        Assert.Equal(new[] { "anita@vendorco.in", "anita.rao@gmail.com" }, c.Emails);   // same address twice counts once
        Assert.Equal("+91 98450 12345", Assert.Single(c.Phones));
        Assert.Equal("Product manager · VendorCo", c.Work);
        Assert.Equal("Met at the Bengaluru expo", c.Notes);
        Assert.Equal("Work", Assert.Single(c.Groups));                                    // system groups left out
    }

    [Fact]
    public void Changes_go_to_Google_with_the_version_they_were_made_on()
    {
        var c = new SavedContact { GivenName = "Ravi", FamilyName = "K", Emails = { "ravi@x.in", " " }, Phones = { "080 1234" }, Company = "X", Etag = "e9" };
        var create = GooglePeopleClient.Body(c, withEtag: false).ToJsonString();
        Assert.DoesNotContain("etag", create);
        Assert.Contains("\"givenName\":\"Ravi\"", create);
        Assert.Contains("\"emailAddresses\":[{\"value\":\"ravi@x.in\"}]", create);
        Assert.Contains("\"organizations\":[{\"name\":\"X\",\"title\":\"\"}]", create);
        Assert.Contains("\"etag\":\"e9\"", GooglePeopleClient.Body(c, withEtag: true).ToJsonString());
    }

    [Fact]
    public void Google_errors_are_said_in_plain_words()
    {
        var off = Assert.IsType<ContactsProblem>(GooglePeopleClient.Problem(HttpStatusCode.Forbidden,
            """{"error":{"code":403,"message":"People API has not been used","details":[{"reason":"SERVICE_DISABLED"}]}}"""));
        Assert.Contains("People API is off", off.Message);
        Assert.False(off.NeedsSignIn);
        Assert.True(Assert.IsType<ContactsProblem>(GooglePeopleClient.Problem(HttpStatusCode.Forbidden,
            """{"error":{"code":403,"message":"Request had insufficient authentication scopes."}}""")).NeedsSignIn);
        Assert.IsType<HttpRequestException>(GooglePeopleClient.Problem(HttpStatusCode.InternalServerError, "oops"));
    }

    [Fact]
    public void A_new_list_from_Google_keeps_changes_not_sent_yet()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.ReplaceSavedContacts("A", new[] { new SavedContact { Resource = "people/c1", Name = "Anita", Emails = { "anita@x.in" } },
                                                new SavedContact { Resource = "people/c2", Name = "Old", Emails = { "old@x.in" } } });
        var anita = store.GetSavedContacts().Single(c => c.Resource == "people/c1");
        anita.Notes = "changed here";
        anita.Pending = PendingContactOp.Update;
        store.SaveLocalContact(anita);
        store.SaveLocalContact(new SavedContact { AccountId = "A", Name = "New here", Emails = { "new@x.in" }, Pending = PendingContactOp.Create });
        // Google: Anita unchanged there, Old deleted there.
        store.ReplaceSavedContacts("A", new[] { new SavedContact { Resource = "people/c1", Name = "Anita", Emails = { "anita@x.in" } } });
        var now = store.GetSavedContacts("A");
        Assert.Equal(new[] { "Anita", "New here" }, now.Select(c => c.Name));
        Assert.Equal("changed here", now[0].Notes);
        Assert.Equal(2, store.PendingContacts("A").Count);
    }

    private static readonly List<(string, string, int, DateTimeOffset, bool)> People = new()
    {
        ("anita@vendorco.in", "Anita Rao", 12, DateTimeOffset.Now.AddDays(-1), true),
        ("anand@other.in", "Anand", 30, DateTimeOffset.Now.AddDays(-2), true),
        ("alerts@bank.in", "", 3, DateTimeOffset.Now, false),
        ("ravi@vendorco.in", "Ravi Kumar", 1, DateTimeOffset.Now.AddDays(-9), true),
    };

    [Fact]
    public void Suggestions_put_the_people_you_write_to_most_first()
    {
        var saved = new List<SavedContact> { new() { Id = 1, Name = "Anita Rao", Emails = { "anita@vendorco.in" } } };
        var hits = ContactSuggest.Rank("an", saved, People);
        Assert.Equal(new[] { "anand@other.in", "anita@vendorco.in" }, hits.Select(h => h.Address));
        Assert.Equal(("Recent", ""), (hits[0].Tag, hits[1].Tag));                     // Anand isn't saved
        Assert.Equal("ravi@vendorco.in", Assert.Single(ContactSuggest.Rank("kum", saved, People)).Address);   // a word of the name
        Assert.Equal(2, ContactSuggest.Rank("vendorco", saved, People).Count);        // the domain
        Assert.Empty(ContactSuggest.Rank("  ", saved, People));
    }

    [Fact]
    public void The_list_shows_saved_contacts_and_people_you_wrote_to()
    {
        var saved = new List<SavedContact>
        {
            new() { Id = 1, Name = "Anita Rao", Emails = { "anita@vendorco.in" }, Company = "VendorCo", Groups = { "Work" } },
            new() { Id = 2, Name = "Bela", Emails = { "bela@x.in" }, Phones = { "+91 98450 12345" }, Groups = { "Family", "Work" } },
        };
        var all = ContactSuggest.Entries(saved, People);
        Assert.Equal(new[] { "Anand", "Anita Rao", "Bela", "Ravi Kumar" }, all.Select(e => e.Name));   // alerts@ was never written to
        Assert.True(all.Single(e => e.Name == "Anand").IsRecent);
        Assert.Equal(12, all.Single(e => e.Name == "Anita Rao").Count);
        Assert.Equal("Anita Rao", Assert.Single(ContactSuggest.Entries(saved, People, "vendorco"), e => !e.IsRecent).Name);   // by company
        Assert.Equal("Bela", Assert.Single(ContactSuggest.Entries(saved, People, "98450")).Name);
        Assert.Equal("Anand", ContactSuggest.Frequent(all).First().Name);
        var groups = ContactSuggest.Groups(all);
        Assert.Equal(new[] { "Family", "Work" }, groups.Select(g => g.Group));
        Assert.Equal(2, groups.Single(g => g.Group == "Work").Members.Count);
    }

    [Fact]
    public void Address_card_offers_Add_to_contacts_or_Show()
    {
        Assert.Contains(HoverMenus.ForAddress("Anita", "anita@x.in", false, false, true, hasContacts: true), o => o.Id == "E6" && o.Label == "Add to contacts");
        Assert.Contains(HoverMenus.ForAddress("Anita", "anita@x.in", false, false, true, hasContacts: true, savedContact: true), o => o.Id == "E6" && o.Label == "Show in Contacts");
        Assert.DoesNotContain(HoverMenus.ForAddress("Anita", "anita@x.in", false, false, true), o => o.Id == "E6");
    }

    private sealed class FakePeople : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public string? Created;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.PathAndQuery;
            Calls.Add(req.Method + " " + req.RequestUri.AbsolutePath);
            string body;
            if (url.Contains("contactGroups"))
                body = """{"contactGroups":[{"resourceName":"contactGroups/work1","groupType":"USER_CONTACT_GROUP","formattedName":"Work"},{"resourceName":"contactGroups/myContacts","groupType":"SYSTEM_CONTACT_GROUP"}]}""";
            else if (req.Method == HttpMethod.Post)
            {
                Created = await req.Content!.ReadAsStringAsync(ct);
                body = """{"resourceName":"people/c9","etag":"n1","names":[{"displayName":"Ravi Kumar","givenName":"Ravi","familyName":"Kumar"}],"emailAddresses":[{"value":"ravi@vendorco.in"}]}""";
            }
            else
                body = "{\"connections\":[" + Anita + (Created == null ? "" : """,{"resourceName":"people/c9","etag":"n1","names":[{"displayName":"Ravi Kumar"}],"emailAddresses":[{"value":"ravi@vendorco.in"}]}""") + "]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Sync_downloads_contacts_and_sends_new_ones()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var google = new FakePeople();
        var acc = new Account { Email = "me@gmail.com", Kind = AccountKind.Gmail, Auth = AuthMethod.OAuth2 };
        var svc = new ContactsService(store, new HttpClient(google), () => new[] { acc }, (_, _) => Task.FromResult("token"),
            _ => $"https://mail.google.com/ {OAuthService.GoogleContacts}");
        await svc.SyncNowAsync(CancellationToken.None);
        Assert.Empty(svc.Problems);
        var anita = Assert.Single(svc.All());
        Assert.Equal(("Anita Rao", "Work"), (anita.Name, anita.Groups.Single()));

        // Made here: listed at once, sent at the next sync, then carries Google's id.
        var ravi = svc.Save(new SavedContact { AccountId = acc.Id, GivenName = " Ravi ", FamilyName = "Kumar", Emails = { "ravi@vendorco.in" } });
        Assert.Equal(("Ravi Kumar", PendingContactOp.Create), (store.GetSavedContact(ravi.Id)!.Name, store.GetSavedContact(ravi.Id)!.Pending));
        await svc.SyncNowAsync(CancellationToken.None);
        Assert.Contains("\"givenName\":\"Ravi\"", google.Created);
        var sent = Assert.Single(svc.All(), c => c.Name == "Ravi Kumar");
        Assert.Equal(("people/c9", PendingContactOp.None), (sent.Resource, sent.Pending));
        Assert.Equal(2, svc.All().Count);

        // A contact that never reached Google is removed here at once.
        var draft = svc.Save(new SavedContact { AccountId = acc.Id, GivenName = "Temp", Emails = { "t@x.in" } });
        svc.Delete(draft);
        Assert.Null(store.GetSavedContact(draft.Id));
    }

    [Fact]
    public async Task Without_the_contacts_permission_the_account_asks_to_sign_in_again()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var acc = new Account { Email = "me@gmail.com", Kind = AccountKind.Gmail, Auth = AuthMethod.OAuth2 };
        var svc = new ContactsService(store, new HttpClient(new FakePeople()), () => new[] { acc }, (_, _) => Task.FromResult("token"), _ => "https://mail.google.com/ openid email profile");
        await svc.SyncNowAsync(CancellationToken.None);
        Assert.True(svc.Problems[acc.Id].NeedsSignIn);
        Assert.Empty(svc.All());
    }
}
