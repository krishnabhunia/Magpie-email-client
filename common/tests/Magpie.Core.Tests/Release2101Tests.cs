using Magpie.Core.Mail;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>
/// 21.0.1 — an email's own window (design OW1) and Trash / Spam. Delete on a copy that is already in Trash or Spam
/// removes it for good, so a window acts on those two folders only when it shows an email of them on purpose.
/// </summary>
public sealed class Release2101Tests
{
    private const long Inbox = 1, Archive = 2, Projects = 3, Trash = 8, Spam = 9;
    private static readonly HashSet<long> Bins = new() { Trash, Spam };
    /// <summary>Every folder an action may touch (not Sent, Drafts, All Mail, Starred, Important).</summary>
    private static readonly long[] Others = { Inbox, Archive, Projects, Trash, Spam };

    private static List<long> Folders(long[] list, long[] copies, bool? binScope = null) =>
        EmailWindow.ActionFolders(list, copies, Others, Bins, binScope ?? EmailWindow.BinScope(list, copies, Bins));

    [Fact]
    public void A_window_opened_from_the_Inbox_acts_on_the_Inbox()
    {
        Assert.False(EmailWindow.BinScope(new[] { Inbox }, new[] { Inbox }, Bins));
        Assert.Equal(new[] { Inbox }, Folders(new[] { Inbox }, new[] { Inbox }));
        // A second copy under a label, or one already in Trash, is left alone.
        Assert.Equal(new[] { Inbox }, Folders(new[] { Inbox }, new[] { Inbox, Projects, Trash }));
        Assert.False(EmailWindow.ClosesWhenBinned(false, new[] { Inbox, Trash }, Bins));
    }

    [Fact]
    public void An_email_moved_to_Trash_by_another_app_is_out_of_reach_of_a_plain_Delete()
    {
        // Opened from the Inbox while it was there: not a window on Trash.
        var binScope = EmailWindow.BinScope(new[] { Inbox }, new[] { Inbox }, Bins);
        Assert.False(binScope);
        // Then the phone deletes it: its only copy is in Trash now. No folder the window acts on holds it.
        var after = EmailWindow.ActionFolders(new[] { Inbox }, new[] { Trash }, Others, Bins, binScope);
        Assert.DoesNotContain(Trash, after);
        Assert.DoesNotContain(Spam, after);
        Assert.Equal(new[] { Inbox, Archive, Projects }, after);
        // And the window closes, as it does for its own Delete. The same for Spam.
        Assert.True(EmailWindow.ClosesWhenBinned(binScope, new[] { Trash }, Bins));
        Assert.True(EmailWindow.ClosesWhenBinned(binScope, new[] { Spam }, Bins));
        Assert.True(EmailWindow.ClosesWhenBinned(binScope, new[] { Trash, Spam }, Bins));
        // Not while the copies are still being listed (none known), and not when it only moved to another folder.
        Assert.False(EmailWindow.ClosesWhenBinned(binScope, Array.Empty<long>(), Bins));
        Assert.False(EmailWindow.ClosesWhenBinned(binScope, new[] { Archive }, Bins));
    }

    [Fact]
    public void An_email_archived_elsewhere_is_still_found_by_the_windows_actions()
    {
        // Opened from the Inbox, archived in the list since: Delete in the window moves the Archive copy to Trash.
        Assert.Equal(new[] { Inbox, Archive, Projects }, Folders(new[] { Inbox }, new[] { Archive }, binScope: false));
        // Opened from Contacts or a reminder (no list): the same folders.
        Assert.Equal(new[] { Inbox, Archive, Projects }, Folders(Array.Empty<long>(), new[] { Projects }, binScope: false));
    }

    [Fact]
    public void A_list_that_spans_every_folder_does_not_hand_Trash_to_the_window()
    {
        // The Pinned and tag views act on every folder, Trash and Spam among them.
        var pinned = new[] { Inbox, Archive, Projects, Trash, Spam };
        Assert.False(EmailWindow.ListIsBin(pinned, Bins));
        Assert.False(EmailWindow.BinScope(pinned, new[] { Inbox }, Bins));
        Assert.Equal(new[] { Inbox, Archive, Projects }, Folders(pinned, new[] { Inbox }));
        // Moved to Trash elsewhere afterwards: still out of reach, and the window closes.
        Assert.Equal(new[] { Inbox, Archive, Projects }, EmailWindow.ActionFolders(pinned, new[] { Trash }, Others, Bins, binScope: false));
        Assert.True(EmailWindow.ClosesWhenBinned(false, new[] { Trash }, Bins));
    }

    [Fact]
    public void A_window_opened_from_Trash_or_Spam_acts_there_as_the_reading_pane_does()
    {
        Assert.True(EmailWindow.ListIsBin(new[] { Trash }, Bins));
        Assert.True(EmailWindow.BinScope(new[] { Trash }, new[] { Trash }, Bins));
        Assert.Equal(new[] { Trash }, Folders(new[] { Trash }, new[] { Trash }));
        Assert.Equal(new[] { Spam }, Folders(new[] { Spam }, new[] { Spam }));
        // An email with a second copy under a label, opened from Trash: the Trash copy is the one acted on.
        Assert.True(EmailWindow.BinScope(new[] { Trash }, new[] { Trash, Projects }, Bins));
        Assert.Equal(new[] { Trash }, Folders(new[] { Trash }, new[] { Trash, Projects }));
        // Such a window stays open: its email is where it was opened.
        Assert.False(EmailWindow.ClosesWhenBinned(true, new[] { Trash }, Bins));
    }

    [Fact]
    public void An_email_that_is_only_in_Trash_opened_from_a_search_is_a_window_on_Trash()
    {
        // The search ran over the Inbox view, with Trash included in what it lists.
        Assert.True(EmailWindow.OnlyInBins(new[] { Trash }, Bins));
        Assert.True(EmailWindow.BinScope(new[] { Inbox }, new[] { Trash }, Bins));
        Assert.Contains(Trash, Folders(new[] { Inbox }, new[] { Trash }));
        Assert.False(EmailWindow.OnlyInBins(new[] { Trash, Inbox }, Bins));
        Assert.False(EmailWindow.OnlyInBins(Array.Empty<long>(), Bins));
        // An account without a Trash or Spam folder: nothing is a bin.
        var none = new HashSet<long>();
        Assert.False(EmailWindow.BinScope(new[] { Inbox }, new[] { Inbox }, none));
        Assert.False(EmailWindow.ListIsBin(Array.Empty<long>(), none));
        Assert.Equal(new[] { Inbox }, EmailWindow.ActionFolders(new[] { Inbox }, new[] { Inbox }, Others, none, false));
    }

    [Fact]
    public void Previous_and_Next_step_over_what_left_the_list()
    {
        Assert.False(EmailWindow.StepsOver(new[] { Inbox }, new[] { Inbox }, Bins));
        Assert.False(EmailWindow.StepsOver(new[] { Inbox }, new[] { Archive }, Bins));      // archived since: still an email to read
        Assert.True(EmailWindow.StepsOver(new[] { Inbox }, Array.Empty<long>(), Bins));     // gone from this PC
        Assert.True(EmailWindow.StepsOver(new[] { Inbox }, new[] { Trash }, Bins));         // deleted since
        Assert.True(EmailWindow.StepsOver(new[] { Inbox }, new[] { Spam }, Bins));          // marked as spam since
        Assert.False(EmailWindow.StepsOver(new[] { Inbox }, new[] { Trash, Projects }, Bins));
        // In Trash itself every email is in Trash: none is stepped over for that.
        Assert.False(EmailWindow.StepsOver(new[] { Trash }, new[] { Trash }, Bins));
        Assert.True(EmailWindow.StepsOver(new[] { Trash }, Array.Empty<long>(), Bins));
    }

    [Fact]
    public void A_list_that_includes_Trash_on_purpose_keeps_its_deleted_emails_within_reach()
    {
        // "Emails with one address" with Trash and Spam included, opened over the Inbox view: an email that was
        // listed from Trash is part of that list, and Previous / Next go to it.
        Assert.False(EmailWindow.StepsOver(new[] { Inbox }, new[] { Trash }, Bins, binnedWhenListed: true));
        Assert.False(EmailWindow.StepsOver(new[] { Inbox }, new[] { Spam }, Bins, binnedWhenListed: true));
        // One that was listed from the Inbox and went to Trash afterwards is stepped over, as in every list.
        Assert.True(EmailWindow.StepsOver(new[] { Inbox }, new[] { Trash }, Bins, binnedWhenListed: false));
        // Gone from this PC altogether: stepped over, however it was listed.
        Assert.True(EmailWindow.StepsOver(new[] { Inbox }, Array.Empty<long>(), Bins, binnedWhenListed: true));
        // The window that lands on it shows an email of Trash on purpose and acts there.
        Assert.True(EmailWindow.BinScope(new[] { Inbox }, new[] { Trash }, Bins));
        Assert.Contains(Trash, Folders(new[] { Inbox }, new[] { Trash }));
    }
}
