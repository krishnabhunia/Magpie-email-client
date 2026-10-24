using System.Net;
using System.Text;
using System.Text.Json;
using Magpie.Core.Auth;
using Magpie.Core.Contacts;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>9.0.0: Contacts (design B4, queue #9, issue #5): Google People API, contacts on this PC, suggestions.</summary>
public class Release900Tests
{
    private const string Anita = """
        {"resourceName":"people/c1","etag":"e1",
         "metadata":{"sources":[{"type":"CONTACT","id":"1a2b","etag":"#src1"}],"objectType":"PERSON"},
         "names":[{"displayName":"Anita Rao","givenName":"Anita","familyName":"Rao"}],
         "emailAddresses":[{"value":"anita@vendorco.in","type":"work","formattedType":"Work"},{"value":"ANITA@vendorco.in"},{"value":"anita.rao@gmail.com","type":"home"}],
         "phoneNumbers":[{"value":"+91 98450 12345","type":"mobile"}],
         "organizations":[{"name":"VendorCo","title":"Product manager"},{"name":"Rotary Club","type":"other"}],
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
        Assert.Contains("\"etag\":\"#src1\"", c.Sources);                                   // kept for the update request
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
        Assert.DoesNotContain("metadata", create);
        var update = GooglePeopleClient.Body(c, withEtag: true).ToJsonString();
        Assert.Contains("\"etag\":\"e9\"", update);
        Assert.DoesNotContain("metadata", update);                                        // nothing known about the sources: left out
        c.Sources = """[{"type":"CONTACT","id":"1a2b","etag":"#src1"}]""";
        Assert.Contains("\"metadata\":{\"sources\":[{\"type\":\"CONTACT\",\"id\":\"1a2b\",\"etag\":\"#src1\"}]}", GooglePeopleClient.Body(c, withEtag: true).ToJsonString());   // updateContact needs it
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

    [Fact]
    public void A_contact_keeps_its_row_across_syncs_and_an_upload_finishing_late_respects_a_newer_change()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.ReplaceSavedContacts("A", new[] { new SavedContact { Resource = "people/c1", Name = "Anita", Emails = { "anita@x.in" } } });
        var id = store.GetSavedContacts("A").Single().Id;
        store.ReplaceSavedContacts("A", new[] { new SavedContact { Resource = "people/c1", Name = "Anita Rao", Emails = { "anita@x.in" } },
                                                new SavedContact { Resource = "people/c2", Name = "Bela", Emails = { "bela@x.in" } } });
        var anita = store.GetSavedContact(id);
        Assert.Equal("Anita Rao", anita!.Name);   // same row, new name — the page's selection and an open edit window still point at it
        Assert.Equal("[]", anita.Sources);
        Assert.Equal(2, store.GetSavedContacts("A").Count);

        // Sent as "Anita Rao", then deleted here before Google answered: the deletion stays, Google's id is remembered.
        anita.Pending = PendingContactOp.Update;
        anita.Updated = DateTimeOffset.Now.AddSeconds(-5);
        store.SaveLocalContact(anita);
        var sent = store.GetSavedContact(id)!;
        var later = store.GetSavedContact(id)!;
        later.Pending = PendingContactOp.Delete;
        later.Updated = DateTimeOffset.Now;
        store.SaveLocalContact(later);
        Assert.True(store.CompleteContactUpload(sent, new SavedContact { Resource = "people/c1", Etag = "e2", Name = "Anita Rao" }));
        var afterwards = store.PendingContacts("A").Single(c => c.Id == id);
        Assert.Equal((PendingContactOp.Delete, "e2"), (afterwards.Pending, afterwards.Etag));

        // A row removed here while its creation was in flight: the caller learns it must delete Google's copy.
        store.DeleteSavedContactRow(id);
        Assert.False(store.CompleteContactUpload(sent, new SavedContact { Resource = "people/c1" }));
        Assert.Single(store.GetSavedContacts("A"));   // Bela is still there
    }

    [Fact]
    public void An_edit_keeps_what_Google_had_and_sends_only_the_changed_fields()
    {
        using var doc = JsonDocument.Parse(Anita);
        var c = GooglePeopleClient.Parse(doc.RootElement, "A", new Dictionary<string, string>());
        Assert.Contains("\"type\":\"work\"", c.Raw);

        // Only the notes changed: only biographies go to Google.
        c.Notes = "Met again in Pune";
        var body = GooglePeopleClient.Body(c, withEtag: true);
        Assert.Equal(new[] { "biographies" }, GooglePeopleClient.ChangedFields(c, body));
        Assert.Equal("Met again in Pune", body["biographies"]![0]!["value"]!.GetValue<string>());

        // Emails reordered + one added, title changed: the work label stays on anita@, the Rotary Club organization survives.
        c.Emails = new List<string> { "anita.rao@gmail.com", "anita@vendorco.in", "new@x.in" };
        c.Title = "Director";
        body = GooglePeopleClient.Body(c, withEtag: true);
        Assert.Equal(new[] { "emailAddresses", "organizations", "biographies" }, GooglePeopleClient.ChangedFields(c, body));
        var emails = body["emailAddresses"]!.AsArray();
        Assert.Equal(("home", "work"), (emails[0]!["type"]!.GetValue<string>(), emails[1]!["type"]!.GetValue<string>()));
        Assert.Null(emails[2]!["type"]);
        var orgs = body["organizations"]!.AsArray();
        Assert.Equal(("Director", "Rotary Club"), (orgs[0]!["title"]!.GetValue<string>(), orgs[1]!["name"]!.GetValue<string>()));
        Assert.Equal("mobile", body["phoneNumbers"]![0]!["type"]!.GetValue<string>());
        Assert.Null(body["names"]![0]!["displayName"]);   // read-only, not sent back

        // Nothing changed: nothing to send.
        var same = GooglePeopleClient.Parse(doc.RootElement, "A", new Dictionary<string, string>());
        Assert.Empty(GooglePeopleClient.ChangedFields(same, GooglePeopleClient.Body(same, withEtag: true)));
    }

    [Fact]
    public void A_refused_upload_keeps_a_change_made_here_since()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.ReplaceSavedContacts("A", new[] { new SavedContact { Resource = "people/c1", Name = "Anita", Emails = { "anita@x.in" } } });
        var sent = store.GetSavedContacts("A").Single();
        sent.Pending = PendingContactOp.Update; sent.Updated = DateTimeOffset.Now.AddSeconds(-3);
        store.SaveLocalContact(sent);
        store.ClearPendingIfUnchanged(sent);
        Assert.Equal(PendingContactOp.None, store.GetSavedContact(sent.Id)!.Pending);   // refused, nothing newer: Google's copy wins

        var newer = store.GetSavedContact(sent.Id)!;
        newer.Pending = PendingContactOp.Update; newer.Updated = DateTimeOffset.Now; newer.Notes = "typed meanwhile";
        store.SaveLocalContact(newer);
        store.ClearPendingIfUnchanged(sent);
        Assert.Equal(PendingContactOp.Update, store.GetSavedContact(sent.Id)!.Pending);   // the newer change is kept for the next round

        // A new contact Google refused is not kept as a ghost after the next list.
        store.SaveLocalContact(new SavedContact { AccountId = "A", Name = "Ghost", Emails = { "g@x.in" }, Pending = PendingContactOp.None });
        store.ReplaceSavedContacts("A", new[] { new SavedContact { Resource = "people/c1", Name = "Anita", Emails = { "anita@x.in" } } });
        Assert.DoesNotContain(store.GetSavedContacts("A"), c => c.Name == "Ghost");
    }

    [Fact]
    public void The_contact_page_lists_only_emails_with_exactly_that_address()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        var fromAnn = Rows.Make("A", inbox, "t1", from: "ann@x.in", subject: "Hi");
        var toAnn = Rows.Make("A", sent, "t2", from: "me@test.local", subject: "Re: Hi");
        toAnn.To = "Ann <ann@x.in>, bob@x.in";
        var ccBare = Rows.Make("A", sent, "t3", from: "me@test.local", subject: "Minutes");
        ccBare.Cc = "bob@x.in, ann@x.in";
        var joann = Rows.Make("A", inbox, "t4", from: "joann@x.in", subject: "Not Ann");
        var toJoann = Rows.Make("A", sent, "t5", from: "me@test.local", subject: "Not Ann either");
        toJoann.To = "Jo <joann@x.in>";
        s.InsertMessages(new[] { fromAnn, toAnn, ccBare, joann, toJoann });
        s.SaveBody(fromAnn.Id, new MessageBody { Text = "see file", Attachments = { new AttachmentInfo { Index = 1, FileName = "a.pdf", Size = 10 } } });
        s.InsertMessages(new[] { Rows.Make("A", inbox, "t1", from: "ann@x.in", subject: "Hi") }); // second message, same thread

        List<string> Find(SearchQuery q) => s.ListThreads(new ListQuery { FolderIds = new[] { inbox, sent }, Search = q }, DateTimeOffset.Now).Select(t => t.ThreadKey).OrderBy(k => k).ToList();
        Assert.Equal(new[] { "t1", "t2", "t3" }, Find(SearchQuery.WithMailbox("ANN@x.in")));
        Assert.Equal(new[] { "t1", "t2", "t3", "t4", "t5" }, Find(SearchQuery.Parse("with:ann@x.in")));   // the typed search stays loose
        Assert.False(SearchQuery.WithMailbox("ann@x.in").IsEmpty);
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
        Assert.Contains("#src1", anita.Sources);

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
