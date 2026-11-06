using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.App.Views;
using Magpie.Core.Mail;
using Magpie.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckOfficePreviewAsync(string root)
    {
        // Dedicated profile: the production window is exercised without using a real user's WebView data.
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "office-preview-profile"))
            .WaitAsync(TimeSpan.FromSeconds(45));
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>Summary</w:t></w:r></w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Amount</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>125</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                </w:body></w:document>
                """);
        }
        package.Position = 0;
        var preview = OfficeDocumentPreview.ReadDocument(package, "safe-preview.docx");
        // Even if unexpected markup reached this view, document scripts and host messaging stay disabled.
        preview = preview with { Html = preview.Html.Replace("</body>", "<script>window.officeUnexpectedScript=true;</script></body>", StringComparison.Ordinal) };
        var originalDark = ThemeManager.IsDark;
        try
        {
            foreach (var dark in new[] { false, true })
            {
                ThemeManager.SetMode(dark ? ThemeMode.Dark : ThemeMode.Light);
                var window = OfficePreviewWindow.Create(null, "safe-preview.docx", preview, environment);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.ShowActivated = false; window.Left = -3000; window.Top = -3000;
                var tabs = ((DockPanel)window.Content).Children.OfType<TabControl>().Single();
                var web = (WebView2)((TabItem)tabs.Items[0]).Content;
                var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var messages = 0;
                var navigationUris = new Dictionary<ulong, string>();
                var navigationState = "No navigation observed";
                web.CoreWebView2InitializationCompleted += (_, e) =>
                {
                    if (!e.IsSuccess) { navigated.TrySetException(e.InitializationException ?? new InvalidOperationException("Office WebView2 initialization failed.")); return; }
                    web.CoreWebView2.NavigationStarting += (_, navigation) =>
                    {
                        // Do not dump inline document contents if a runtime reports a data URI.
                        var uri = navigation.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                            ? "data:[inline document]" : navigation.Uri[..Math.Min(navigation.Uri.Length, 160)];
                        navigationUris[navigation.NavigationId] = uri;
                        navigationState = $"starting id={navigation.NavigationId}; uri={uri}";
                        Console.WriteLine("OFFICE_PREVIEW_SMOKE " + navigationState);
                    };
                    web.CoreWebView2.NavigationCompleted += async (_, navigation) =>
                    {
                        navigationUris.TryGetValue(navigation.NavigationId, out var uri);
                        navigationState = $"completed id={navigation.NavigationId}; uri={uri ?? "[bootstrap start not observed]"}; "
                            + $"success={navigation.IsSuccess}; status={navigation.WebErrorStatus}";
                        Console.WriteLine("OFFICE_PREVIEW_SMOKE " + navigationState);
                        if (!navigation.IsSuccess)
                        {
                            // Initialization can cancel its blank bootstrap document when production calls
                            // NavigateToString. Cancellation alone is not readiness or a test failure: an actual
                            // successful preview with the expected DOM must still arrive within the same timeout.
                            // A blocked real preview can therefore never turn this fixture green.
                            if (navigation.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                            navigated.TrySetException(new InvalidOperationException("Office preview navigation failed: " + navigationState));
                            return;
                        }
                        try
                        {
                            // Ignore an environment's initial blank document; wait for the generated preview itself.
                            var rendered = await web.CoreWebView2.ExecuteScriptAsync(
                                "document.title==='Magpie read-only Office preview'&&document.querySelector('h2')?.textContent==='Summary'&&document.querySelectorAll('table td').length===2")
                                .WaitAsync(TimeSpan.FromSeconds(20));
                            if (rendered == "true") navigated.TrySetResult(true);
                        }
                        catch (Exception ex) { navigated.TrySetException(ex); }
                    };
                    web.CoreWebView2.ProcessFailed += (_, failure) =>
                        navigated.TrySetException(new InvalidOperationException("Office preview process failed: " + failure.ProcessFailedKind));
                    web.CoreWebView2.WebMessageReceived += (_, _) => messages++;
                };
                Console.WriteLine("OFFICE_PREVIEW_SMOKE phase=show; dark=" + dark);
                window.Show();
                try
                {
                    try { await navigated.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
                    catch (TimeoutException ex)
                    {
                        throw new InvalidOperationException("Office preview never rendered its required title and document contents. Last navigation: " + navigationState, ex);
                    }
                    var core = web.CoreWebView2;
                    Task<string> Script(string script) => core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(20));
                    Assert(await Script("document.querySelector('h2').textContent") == "\"Summary\""
                        && await Script("document.querySelectorAll('table td').length") == "2"
                        && await Script("document.querySelector('table').textContent.includes('125')") == "true",
                        "Structured Office preview renders headings and tables in " + (dark ? "dark" : "light"));
                    Assert(JsonSerializer.Deserialize<string>(await Script("getComputedStyle(document.body).backgroundColor"))
                        == (dark ? "rgb(24, 28, 32)" : "rgb(255, 255, 255)"), "Structured Office preview follows the app theme");
                    Assert(!core.Settings.IsScriptEnabled && !core.Settings.IsWebMessageEnabled && !core.Settings.AreHostObjectsAllowed,
                        "Office preview disables document scripts, host messages and host objects");
                    Assert(await Script("typeof window.officeUnexpectedScript") == "\"undefined\"" && messages == 0,
                        "Unexpected Office markup cannot execute document scripts or post host messages");
                    Assert(AutomationProperties.GetName(web) == "Read-only Office document preview", "Office preview has a screen-reader name");
                    var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    const string forbidden = "https://office-preview.invalid/navigation";
                    core.NavigationStarting += (_, e) =>
                    {
                        if (e.Uri == forbidden) blocked.TrySetResult(e.Cancel);
                    };
                    core.Navigate(forbidden);
                    Assert(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Office preview cancels external navigation before any network request");
                    tabs.SelectedIndex = 1;
                    var plain = (TextBox)((TabItem)tabs.Items[1]).Content;
                    Assert(plain.IsReadOnly && plain.Text.Contains("Summary") && plain.Text.Contains("125"),
                        "Office preview retains a selectable read-only plain-text view");
                }
                finally { window.Close(); }
            }
        }
        finally { ThemeManager.SetMode(originalDark ? ThemeMode.Dark : ThemeMode.Light); }
        Console.WriteLine("OFFICE_PREVIEW_SMOKE_PASSED");
    }
}
