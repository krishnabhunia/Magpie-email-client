using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckSecureMailAsync(string root, MailEngine originalEngine)
    {
        using var engine = new MailEngine(new AppPaths(Path.Combine(root, "secure-mail")), new DpapiProtector(), new RejectHttp());
        var account = new Account { Id = "secure", Email = "secure@example.test", Enabled = false };
        engine.Config.Accounts.Add(account); engine.Config.Ai.Enabled = false; AppServices.Engine = engine;
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Native secure mail smoke", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddEmailAddress(account.Email); request.CertificateExtensions.Add(names.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            var pkcs12 = certificate.Export(X509ContentType.Pkcs12, "native-fixture");
            try { engine.SecureMail.Keys.Import(account.Id, account.Email, MailSecurityFormat.Smime, pkcs12, "native-fixture", true); }
            finally { CryptographicOperations.ZeroMemory(pkcs12); }
            foreach (var dark in new[] { false, true })
            {
                ThemeManager.SetMode(dark ? Magpie.Core.Settings.ThemeMode.Dark : Magpie.Core.Settings.ThemeMode.Light);
                var draft = new Draft { AccountId = account.Id, To = account.Email, Subject = "Native secure fixture", Html = "<p>Private native fixture</p>" };
                var compose = new ComposeWindow(draft, null);
                var vm = (ComposeViewModel)compose.DataContext;
                try
                {
                    vm.GetHtml = () => Task.FromResult(draft.Html);
                    vm.GetNewText = () => Task.FromResult("Private native fixture");
                    vm.SecurityFormat = "OpenPGP"; vm.SignMessage = true; vm.EncryptMessage = true;
                    Assert(draft.Security.Format == MailSecurityFormat.OpenPgp && draft.Security.Sign && draft.Security.Encrypt && vm.Dirty,
                        "Security options update the editable draft and mark it dirty");
                    vm.SecurityFormat = "S/MIME";
                    var studio = (Border)compose.FindName("StudioPanel"); studio.Visibility = Visibility.Visible;
                    var tabs = ((DockPanel)studio.Child).Children.OfType<TabControl>().Single(); tabs.SelectedIndex = tabs.Items.Count - 1;
                    Flush(compose);
                    var optionsPanel = (StackPanel)((ScrollViewer)((TabItem)tabs.Items[tabs.Items.Count - 1]).Content).Content;
                    Assert(optionsPanel.Children.OfType<CheckBox>().Any(c => Equals(c.Content, "Sign message") && c.IsChecked == true)
                        && optionsPanel.Children.OfType<CheckBox>().Any(c => Equals(c.Content, "Encrypt message") && c.IsChecked == true),
                        "Native compose security checkboxes reflect selected options in both themes");
                    var size = await vm.MeasureDraftSizeAsync();
                    var protectedMessage = engine.SecureMail.PrepareOutgoing(Composer.Build(draft, account), account, draft.Security, DateTimeOffset.UtcNow);
                    Assert(size.Bytes > 0 && SecureMailService.IsProtected(protectedMessage.Message.Body), "Message-size review builds actual protected MIME");
                    var destination = Path.Combine(root, "secure-export-" + dark + ".eml");
                    Assert(await vm.ExportEmlAsync(destination), "Secure compose supports protected .eml export");
                    using var exported = Composer.FromBytes(File.ReadAllBytes(destination));
                    Assert(SecureMailService.IsProtected(exported.Body) && exported.Headers[SecureMailOptions.DraftHeader] == null,
                        "Export contains protected MIME and no private draft intent header");
                    File.Delete(destination);
                }
                finally { vm.Dirty = false; vm.Detach(); compose.Close(); }
                var manager = SecureMailKeysWindow.Create(null, engine, account.Id);
                try
                {
                    manager.Left = -3000; manager.Top = -3000; manager.ShowActivated = false; manager.Show(); manager.UpdateLayout();
                    var list = ((DockPanel)manager.Content).Children.OfType<ListView>().Single();
                    Assert(list.Items.Count == 1 && ((MailKeyInfo)list.Items[0]).HasPrivateKey,
                        "Native key manager lists the imported account identity");
                    Assert(Descendants(manager).OfType<PasswordBox>().Count() == 1
                        && Descendants(manager).OfType<Button>().Any(b => Equals(b.Content, "Export public key…")),
                        "Key manager uses a password field and exposes only public-key export");
                }
                finally { manager.Close(); }
            }

            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "secure-reader-profile"))
                .WaitAsync(TimeSpan.FromSeconds(45));
            var source = Composer.Build(new Draft { AccountId = account.Id, To = account.Email, Subject = "Secure viewer", Html =
                "<h2 id=\"secure-content\">Private readable fixture</h2><script>window.secretScript=true;</script><img src=\"https://remote.invalid/secret\"><a href=\"https://remote.invalid/link\">External</a>" }, account);
            var outgoing = engine.SecureMail.PrepareOutgoing(source, account, new() { Sign = true, Encrypt = true }, DateTimeOffset.UtcNow);
            var result = engine.SecureMail.Open(outgoing.Message, account.Id);
            var renderFilesBefore = Directory.Exists(WebHost.RenderDir) ? Directory.GetFiles(WebHost.RenderDir).Order().ToArray() : Array.Empty<string>();
            var reader = SecureMessageWindow.Create(null, result, environment);
            reader.Left = -3000; reader.Top = -3000; reader.ShowActivated = false;
            var readerTabs = ((DockPanel)reader.Content).Children.OfType<TabControl>().Single();
            var web = (WebView2)((TabItem)readerTabs.Items[0]).Content;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong documentNavigation = 0;
            var forbiddenRequests = 0;
            web.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) { ready.TrySetException(e.InitializationException ?? new InvalidOperationException("Secure reader initialization failed")); return; }
                web.CoreWebView2.NavigationStarting += (_, navigation) => { if (navigation.Uri == SecureMessageWindow.DocumentUri) documentNavigation = navigation.NavigationId; };
                web.CoreWebView2.NavigationCompleted += (_, navigation) =>
                {
                    if (navigation.NavigationId != documentNavigation || documentNavigation == 0) return;
                    if (navigation.IsSuccess) ready.TrySetResult(true);
                    else ready.TrySetException(new InvalidOperationException("Secure document navigation failed: " + navigation.WebErrorStatus));
                };
                web.CoreWebView2.WebResourceResponseReceived += (_, response) =>
                { if (response.Request.Uri.StartsWith("https://remote.invalid/", StringComparison.Ordinal) && response.Response.StatusCode != 403) forbiddenRequests++; };
            };
            reader.Show();
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
                var core = web.CoreWebView2;
                Task<string> Script(string script) => core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(20));
                Assert(core.Profile.IsInPrivateModeEnabled, "Decrypted message uses an InPrivate browser profile");
                Assert(!core.Settings.IsScriptEnabled && !core.Settings.IsWebMessageEnabled && !core.Settings.AreHostObjectsAllowed
                    && !core.Settings.AreDevToolsEnabled && !core.Settings.AreDefaultContextMenusEnabled,
                    "Decrypted reader disables message scripts, host messages, host objects, developer tools and browser export menus");
                Assert(await Script("document.body.textContent.includes('Private readable fixture')") == "true"
                    && await Script("typeof window.secretScript") == "\"undefined\"", "Sanitized decrypted body renders without executing its script");
                Assert(await Script("[...document.images].every(i=>!i.src.startsWith('http'))") == "true" && forbiddenRequests == 0,
                    "Decrypted email cannot load remote pictures");
                foreach (var uri in new[] { "https://remote.invalid/navigation", "data:text/html,untrusted", "about:blank" })
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ulong blockedId = 0;
                    EventHandler<CoreWebView2NavigationStartingEventArgs> onNavigation = (_, e) => { if (e.Uri == uri) { blockedId = e.NavigationId; cancelled.TrySetResult(e.Cancel); } };
                    EventHandler<CoreWebView2NavigationCompletedEventArgs> onCompleted = (_, e) => { if (e.NavigationId == blockedId) completed.TrySetResult(!e.IsSuccess); };
                    core.NavigationStarting += onNavigation;
                    core.NavigationCompleted += onCompleted;
                    try
                    {
                        core.Navigate(uri);
                        Assert(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20)) && await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)),
                            "Secure reader rejects navigation outside its memory document");
                    }
                    finally { core.NavigationStarting -= onNavigation; core.NavigationCompleted -= onCompleted; }
                }
                Assert(AutomationProperties.GetName(web) == "Isolated secure message", "Secure reader exposes its accessibility name");
                var renderFilesAfter = Directory.Exists(WebHost.RenderDir) ? Directory.GetFiles(WebHost.RenderDir).Order().ToArray() : Array.Empty<string>();
                Assert(renderFilesBefore.SequenceEqual(renderFilesAfter),
                    "Secure reader does not publish decrypted HTML to the render cache");
            }
            finally { reader.Close(); }
            Assert(((TextBox)((TabItem)readerTabs.Items[1]).Content).Text.Length == 0, "Closing the secure reader clears its native plaintext view");
        }
        finally { AppServices.Engine = originalEngine; }
        Console.WriteLine("SECURE_MAIL_SMOKE_PASSED");
    }
}
