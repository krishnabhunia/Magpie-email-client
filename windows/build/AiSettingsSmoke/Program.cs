using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Ai;
using Magpie.Core.Security;
using Magpie.Core.Settings;

namespace AiSettingsSmoke;

/// <summary>Exercises the real WPF settings bindings and asynchronous editor against fake HTTP and disposable data.</summary>
internal static class Program
{
    private static int _assertions;
    [STAThread]
    public static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "magpie-ai-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var http = new FakeProvider();
            using var engine = new MailEngine(new AppPaths(root), new TestProtector(), http);
            AppServices.Engine = engine;
            var connection = new AiConnection { Id = "claude", Name = "Claude", Provider = AiProviderKind.Anthropic,
                Endpoint = "https://api.anthropic.com", Model = "claude-future" };
            engine.Config.Ai.Connections = new() { connection, new() { Id = "openai", Name = "Existing OpenAI", Endpoint = "https://api.openai.com/v1", Model = "gpt-4.1-mini" } };
            engine.Config.Ai.ActiveId = connection.Id;
            engine.Config.Ai.UseActive();
            engine.Config.Ai.Enabled = true;
            engine.Vault.Set(SecretVault.AiKeyFor(connection.Id), "saved-claude-key");
            engine.Vault.Set(SecretVault.AiKeyFor("openai"), "saved-openai-key");
            engine.Settings.Save();
            var before = File.ReadAllText(engine.Paths.Settings);
            var vm = new SettingsViewModel("AI");
            Assert(vm.ApiKey == "saved-claude-key", "Key loads from this connection only");
            Run(vm.RefreshAiModelsCommand.ExecuteAsync(null));
            Assert(vm.DiscoveredAiModels.Count == 2 && vm.PublishedAiModels.Count == 0, "Discovered models require explicit enabling");
            vm.DiscoveredAiModels[0].Enabled = true;
            Assert(vm.PublishedAiModels.SequenceEqual(new[] { "claude-future" }), "Enabling publishes model to dropdown");
            PumpUntil(() => vm.HasChanges);
            vm.DiscoveredAiModels[0].Enabled = false;
            Assert(vm.PublishedAiModels.Count == 0 && vm.Model == "claude-future", "Disabling dropdown entry preserves selected/manual model");
            vm.DiscoveredAiModels[1].Enabled = true;
            vm.PublishedAiModel = "claude-other";
            Assert(vm.Model == "claude-other", "Dropdown selects the model actually used by generation");
            Run(vm.TestConnectionCommand.ExecuteAsync(null));
            Assert(vm.TestOk, "Candidate model passes connection test");
            Assert(File.ReadAllText(engine.Paths.Settings) == before, "Refresh, enable and test do not save settings");
            Assert(engine.Config.Ai.Model == "claude-future", "Testing candidate never changes active model");
            Assert(engine.Vault.Get(SecretVault.AiKeyFor(connection.Id)) == "saved-claude-key", "Testing never overwrites vault");
            Assert(!http.Bodies.Any(b => b.Contains("PRIVATE_EMAIL")), "Only synthetic test prompts are sent");

            // Simulate a server finishing despite cancellation: old results must not land on another connection.
            http.Pending = new TaskCompletionSource<HttpResponseMessage>();
            var refresh = vm.RefreshAiModelsCommand.ExecuteAsync(null);
            Assert(!vm.TestConnectionCommand.CanExecute(null) && !vm.RefreshAiModelsCommand.CanExecute(null), "Both commands block overlapping requests");
            vm.SelectedAiConnection = vm.AiConnections.Single(c => c.Id == "openai");
            Assert(vm.ApiKey == "saved-openai-key" && !vm.IsAnthropic && vm.PublishedAiModels.Count == 0, "Switching connections isolates credentials and catalog");
            http.FinishPending();
            Run(refresh);
            Assert(!vm.TestOk && vm.TestStatus.Length == 0, "Late refresh cannot mark another connection tested");
            vm.SelectedAiConnection = vm.AiConnections.Single(c => c.Id == "claude");
            http.Pending = new TaskCompletionSource<HttpResponseMessage>();
            var test = vm.TestConnectionCommand.ExecuteAsync(null);
            vm.Model = "edited-during-test";
            http.FinishPending(testReply: true);
            Run(test);
            Assert(!vm.TestOk, "Model edits invalidate in-flight connection test");
            vm.Model = "claude-other";
            vm.ApiKey = "new-candidate-key";
            vm.ApiKeyChanged = true;
            vm.AiCredentialsChanged();
            Assert(vm.PublishedAiModels.Count == 0, "Key edits invalidate the old workspace catalog");
            Assert(engine.Vault.Get(SecretVault.AiKeyFor(connection.Id)) == "saved-claude-key", "Key edits stay unsaved until Apply");
            Run(vm.RefreshAiModelsCommand.ExecuteAsync(null));
            vm.DiscoveredAiModels[0].Enabled = true;
            vm.DiscoveredAiModels[1].Enabled = true;
            var modelsBeforeFailure = vm.DiscoveredAiModels.Select(m => m.Info.Id).ToArray();
            http.Fail = true;
            Run(vm.RefreshAiModelsCommand.ExecuteAsync(null));
            Assert(vm.DiscoveredAiModels.Select(m => m.Info.Id).SequenceEqual(modelsBeforeFailure), "Failed refresh keeps last good list");
            Assert(vm.TestStatus.StartsWith("The API key was rejected"), "Failures show readable errors");
            http.Fail = false;
            vm.Model = "claude-opus-4-6";
            Assert(vm.TemperatureUsed, "Older Claude model permits creativity setting");
            vm.Temperature = 0.7;
            http.RefuseTemperatureOnce = true;
            Run(vm.TestConnectionCommand.ExecuteAsync(null));
            Assert(vm.TestOk && !vm.TemperatureUsed, "Explicit temperature rejection recovers and disables creativity for this model");
            Assert(vm.TestStatus.Contains("sent without temperature"), "Test explains effective parameters");
            Assert(engine.Config.Ai.Active!.Unsupported.Count == 0, "Testing learned parameters stays unsaved until Apply");
            vm.Model = "claude-other";

            // Exercise the actual save path without modifying the host's startup registration.
            var save = typeof(SettingsViewModel).GetMethod("SaveAiConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            save.Invoke(vm, new object[] { engine.Config.Ai });
            engine.Settings.Save();
            Assert(engine.Config.Ai.Model == "claude-other", "Saved selected model becomes active");
            Assert(engine.Vault.Get(SecretVault.AiKeyFor(connection.Id)) == "new-candidate-key", "Apply stores changed key in the correct vault slot");
            Assert(engine.Vault.Get(SecretVault.AiKeyFor("openai")) == "saved-openai-key", "Other provider's key is preserved");
            Assert(engine.Config.Ai.Active!.Temperature == 0.7 && engine.Config.Ai.Active.Unsupported.Contains("claude-opus-4-6:temperature"), "Apply persists creativity and model-specific learned settings");
            Assert(!File.ReadAllText(engine.Paths.Settings).Contains("candidate-key"), "Settings never contain API credentials");
            Assert(!File.ReadAllText(engine.Paths.Secrets).Contains("candidate-key"), "Vault stores encrypted credentials");
            vm.Detach();
            var reopened = new SettingsViewModel("AI");
            Assert(reopened.PublishedAiModels.Count == 2 && reopened.Model == "claude-other", "Saved catalog and model survive reopening");
            reopened.Model = "claude-opus-4-6";
            Assert(!reopened.TemperatureUsed && reopened.Temperature == 0.7, "Reopening retains model-specific parameter preferences");
            reopened.Provider = AiProviderKind.OpenAI;
            Assert(reopened.ApiKey.Length == 0 && reopened.ApiKeyChanged, "Changing provider clears credential rather than reusing it");
            reopened.Detach();

            var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "captures");
            Directory.CreateDirectory(output);
            foreach (var theme in new[] { "Light", "Dark" })
            {
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Magpie;component/Themes/" + theme + ".xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Magpie;component/Styles/Magpie.xaml") });
                var window = new SettingsWindow("AI") { ShowActivated = false, Left = -3000, Top = -3000 };
                window.Show();
                PumpUntil(() => window.IsLoaded);
                var actualVm = (SettingsViewModel)window.DataContext;
                var providerSection = (FrameworkElement)window.FindName("RowAiProvider");
                providerSection.BringIntoView();
                window.UpdateLayout();
                var dropdown = Descendants(window).OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, actualVm.PublishedAiModels));
                Assert(dropdown.Items.Count == 2, theme + " actual dropdown displays enabled discovered models");
                dropdown.SelectedItem = "claude-future";
                Assert(actualVm.Model == "claude-future", theme + " dropdown binding updates exact model ID");
                var keyBox = (PasswordBox)window.FindName("ApiKeyBox");
                Assert(keyBox.Password == "new-candidate-key", theme + " masked key loads from vault");
                Capture(window, Path.Combine(output, theme + "-ai-settings.png"));
                var testButton = Descendants(window).OfType<Button>().Single(b => ReferenceEquals(b.Command, actualVm.TestConnectionCommand));
                testButton.BringIntoView();
                window.UpdateLayout();
                Capture(window, Path.Combine(output, theme + "-ai-settings-test.png"));
                window.Close();
            }
            Assert(engine.Config.Ai.Model == "claude-other", "Closing Settings discards unsaved dropdown edits");
            Console.WriteLine("AI_SETTINGS_SMOKE_PASS assertions=" + _assertions);
            app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // root is an absolute, randomly allocated smoke-only directory created above.
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Run(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert(done(), "UI operation completed before deadline");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _assertions++; }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }
    private static void Capture(Window window, string path)
    {
        var target = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5a)).ToArray();
        public byte[] Unprotect(byte[] cipher) => Protect(cipher);
    }
    private sealed class FakeProvider : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public TaskCompletionSource<HttpResponseMessage>? Pending { get; set; }
        public bool Fail { get; set; }
        public bool RefuseTemperatureOnce { get; set; }
        private static HttpResponseMessage Models() => new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"claude-future","display_name":"Claude Future"},{"id":"claude-other","display_name":"Claude Other"}],"has_more":false}""") };
        private static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"OK\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream") };
        public void FinishPending(bool testReply = false) { var pending = Pending!; Pending = null; pending.SetResult(testReply ? Reply() : Models()); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            if (Pending != null) return await Pending.Task; // deliberately ignore cancellation to verify stale-result protection
            if (Fail) return new(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            if (RefuseTemperatureOnce && request.Method == HttpMethod.Post)
            {
                RefuseTemperatureOnce = false;
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"temperature is deprecated and unsupported"}}""") };
            }
            return request.Method == HttpMethod.Get ? Models() : Reply();
        }
    }
}
