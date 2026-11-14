using System.IO;
using System.Windows;
using System.Windows.Controls;
using Magpie.App.Views;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>Design HD1 (option A): the real Headers window opens in this theme with its table, find box, chips and plain text.</summary>
    private static void CheckHeadersWindow(Window owner, string output, string theme)
    {
        var row = new MessageRow
        {
            Id = 7, AccountId = "A", Subject = "Handover schedule — follow-up", FromName = "Rajesh Mehta", FromAddress = "rajesh@buildwell.example",
            Date = new DateTimeOffset(2026, 11, 13, 15, 34, 0, TimeSpan.Zero), MessageId = "7f3a9c21.4410@buildwell.example",
        };
        var lines = new List<HeaderLine>
        {
            new("Delivered-To", "krishna@example.com"),
            new("Received", "by mx.example.com with ESMTPS id k9si4410; Fri, 13 Nov 2026 15:34:09 +0000"),
            new("Received", "from out2.buildwell.example (203.0.113.24) by mx.example.com; Fri, 13 Nov 2026 15:34:07 +0000"),
            new("Received", "from desk-rm.buildwell.example (198.51.100.7) by out2.buildwell.example; Fri, 13 Nov 2026 15:34:02 +0000"),
            new("Authentication-Results", "mx.example.com; spf=pass smtp.mailfrom=buildwell.example; dkim=fail header.d=buildwell.example; dmarc=pass"),
            new("From", "Rajesh Mehta <rajesh@buildwell.example>"),
            new("Subject", "Handover schedule — follow-up"),
            new("Message-ID", "<7f3a9c21.4410@buildwell.example>"),
        };
        var window = new HeadersWindow(row, lines, owner) { ShowActivated = false, Left = -4000, Top = -4000 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert(window.Title == "Email headers", "Headers window title");
            var rows = (StackPanel)window.FindName("HeaderRows");
            var count = (TextBlock)window.FindName("HeaderCount");
            Assert(rows.Children.Count == 8, "Headers table has one row per header line");
            Assert(count.Text == "8 header lines", "Headers window counts its lines");
            var chips = (WrapPanel)window.FindName("HeaderChips");
            var chipWords = Descendants(chips).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert(chips.Visibility == Visibility.Visible && chipWords.Contains("SPF passed") && chipWords.Contains("DKIM failed") && chipWords.Contains("DMARC passed"),
                "Headers window shows the mail server's SPF, DKIM and DMARC results in words");
            Assert(chipWords.Contains("Passed through 3 servers in 7 seconds"), "Headers window says how the email travelled");
            Assert(Descendants(rows).OfType<TextBox>().Any(t => t.IsReadOnly && t.Text == "<7f3a9c21.4410@buildwell.example>"), "A header value can be selected and copied");
            Assert(((Button)window.FindName("HeaderCopy")).IsEnabled && ((Button)window.FindName("HeaderSave")).IsEnabled, "Copy all headers and Save are ready");
            Capture(window, Path.Combine(output, theme + "-headers-table.png"));

            var find = (TextBox)window.FindName("HeaderFind");
            find.Text = "received"; window.UpdateLayout();
            Assert(rows.Children.Count == 3 && count.Text == "3 of 8 header lines", "The find box narrows the header lines");
            find.Text = "no such text"; window.UpdateLayout();
            Assert(rows.Children.Count == 0 && ((TextBlock)window.FindName("HeaderStatus")).Text == "No header line holds \"no such text\".", "Nothing found is said in words");
            find.Text = ""; window.UpdateLayout();
            Assert(rows.Children.Count == 8, "Clearing the find box shows every header line again");

            var plain = (TextBox)window.FindName("HeaderPlain");
            ((RadioButton)window.FindName("HeaderAsText")).IsChecked = true; window.UpdateLayout();
            Assert(plain.Visibility == Visibility.Visible && plain.Text.Split("\r\n").Length == 8 && plain.Text.StartsWith("Delivered-To: krishna@example.com\r\n", StringComparison.Ordinal),
                "Plain text shows Name: value, one header per line");
            Assert(window.AllText == plain.Text, "Copy all headers and Save write what Plain text shows");
            Capture(window, Path.Combine(output, theme + "-headers-plain.png"));
            ((RadioButton)window.FindName("HeaderAsTable")).IsChecked = true; window.UpdateLayout();
            Assert(plain.Visibility == Visibility.Collapsed && rows.Children.Count == 8, "Table comes back");
        }
        finally { window.Close(); }

        // An email without server lines has no chips row.
        var bare = new HeadersWindow(row, new List<HeaderLine> { new("Subject", "Hello") }, owner) { ShowActivated = false, Left = -4000, Top = -4000 };
        try
        {
            bare.Show();
            Assert(((WrapPanel)bare.FindName("HeaderChips")).Visibility == Visibility.Collapsed, "No checks and no servers: no chips");
            Assert(((TextBlock)bare.FindName("HeaderCount")).Text == "1 header line", "One header line is counted in the singular");
        }
        finally { bare.Close(); }
    }
}
