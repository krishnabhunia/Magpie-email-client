using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Magpie.Core;
using NativeWebView.Core;
using NativeWebView.Platform.macOS;
using WebView = NativeWebView.Controls.NativeWebView;

namespace Magpie.Mac.Services;

/// <summary>
/// The native macOS web view (WKWebView, through the NativeWebView Avalonia control) for Magpie's own pages: the
/// reading pane (Core's reader shell + HtmlRenderer pages) and the compose editor (Core's EditorPage). Pages talk back
/// with <c>window.chrome.webview.postMessage</c>, which NativeWebView provides on the Mac as on Windows. Links never
/// navigate the view: web links open in the default browser, mailto: links in a new message. Elsewhere (Linux test
/// runs) a plain placeholder stands in, so windows still build without a display.
/// </summary>
public sealed class WebSurface : Decorator
{
    private readonly WebView? _web;
    private Uri? _pending;
    private bool _initialising;
    private string _ownFolder = "";
    private static int _pageNumber;

    /// <summary>A message the page posted (its JSON text).</summary>
    public event Action<string>? Message;
    /// <summary>A page finished loading (true) or failed (false).</summary>
    public event Action<bool>? PageLoaded;
    /// <summary>A mailto: link was clicked.</summary>
    public event Action<string>? MailtoClicked;

    public bool IsNative => _web != null;

    /// <summary>The real WKWebView only in the Mac app itself: not on other systems, not under Avalonia's headless
    /// test platform (no desktop lifetime there), and not when MAGPIE_HEADLESS=1 (set by the tests).</summary>
    public static bool UseNativeView() =>
        OperatingSystem.IsMacOS()
        && Environment.GetEnvironmentVariable("MAGPIE_HEADLESS") != "1"
        && Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

    /// <summary>The page that has the keyboard right now (null when an ordinary control has it).</summary>
    public static WebSurface? Focused { get; private set; }

    private const string FocusScript = """
        (function(){
          function tell(t){ try{ window.chrome.webview.postMessage(JSON.stringify({t:t})); }catch(e){} }
          window.addEventListener('focus', function(){ tell('magpie-focus'); });
          // Clicking into an email (its own frame) blurs the page but keeps the keyboard in it.
          window.addEventListener('blur', function(){ setTimeout(function(){ if(!document.hasFocus()) tell('magpie-blur'); }, 0); });
        })();
        """;

    private void OnMessage(string json)
    {
        if (json.Contains("\"magpie-focus\"", StringComparison.Ordinal)) { Focused = this; return; }
        if (json.Contains("\"magpie-blur\"", StringComparison.Ordinal)) { if (Focused == this) Focused = null; return; }
        Message?.Invoke(json);
    }

    public WebSurface()
    {
        if (!UseNativeView())
        {
            Child = new TextBlock
            {
                Text = "The reading pane needs the Mac app.", Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }
        try
        {
            // Loads the macOS backend assembly, then registers it once (the runtime remembers it is registered).
            _ = typeof(NativeWebViewPlatformMacOSModule);
            NativeWebViewRuntime.EnsureCurrentPlatformRegistered();
            var config = new NativeWebViewInstanceConfiguration();
            // Nothing to keep between runs (no cookies, no site data), so a private (non-persistent) data store.
            config.ControllerOptions.IsInPrivateModeEnabled = true;
            config.ControllerOptions.IsJavaScriptEnabled = true;
            config.ControllerOptions.IsPasswordAutosaveEnabled = false;
            config.ControllerOptions.IsGeneralAutofillEnabled = false;
            config.ControllerOptions.ProfileName = "magpie";
            // Tells Magpie when the page has the keyboard, so the Edit menu (⌘C, ⌘V …) acts on the page then.
            config.DocumentStartScripts.Add(new NativeWebViewDocumentStartScript(FocusScript, NativeWebViewScriptFrameScope.MainFrame));
            _web = new WebView(config) { IsDevToolsEnabled = false, IsStatusBarEnabled = false };
            _web.WebMessageReceived += (_, e) => OnMessage(string.IsNullOrEmpty(e.Message) ? e.Json ?? "" : e.Message);
            _web.NavigationStarted += OnNavigationStarted;
            _web.NewWindowRequested += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
            _web.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) Log.Warn("web page did not load: " + e.Error);
                PageLoaded?.Invoke(e.IsSuccess);
            };
            Child = _web;
            AttachedToVisualTree += async (_, _) => await InitialiseAsync();
        }
        catch (Exception ex)
        {
            Log.Error("web view could not be created", ex);
            _web = null;
            Child = new TextBlock { Text = "The reading pane couldn't start: " + ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(16) };
        }
    }

    private async Task InitialiseAsync()
    {
        if (_web == null || _web.IsInitialized || _initialising) return;
        _initialising = true;
        try { await _web.InitializeAsync(); }
        catch (Exception ex) { Log.Error("web view could not start", ex); }
        finally { _initialising = false; }
        if (_pending is { } p) { _pending = null; _web.Navigate(p); }
    }

    /// <summary>Shows a page of Magpie's own (the reader shell, the editor).</summary>
    public void Load(string html, string name)
    {
        if (_web == null) return;
        var uri = PageUri(html, name);
        if (_web.IsInitialized) _web.Navigate(uri);
        else _pending = uri;
    }

    /// <summary>Small pages go in a data: URL; a big one is written to the web view folder (a URL can't be that long).</summary>
    private Uri PageUri(string html, string name)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        if (b64.Length < 60_000) return new Uri("data:text/html;charset=utf-8;base64," + b64);
        var folder = Path.Combine(AppServices.Engine.Paths.WebViewData, "pages");
        Directory.CreateDirectory(folder);
        _ownFolder = folder;
        var path = Path.Combine(folder, $"{name}-{Interlocked.Increment(ref _pageNumber)}.html");
        File.WriteAllText(path, html);
        try
        {
            foreach (var old in Directory.GetFiles(folder, name + "-*.html").Where(f => f != path)) File.Delete(old);
        }
        catch { }
        return new Uri(path);
    }

    /// <summary>Runs a script in the page; returns its result as JSON ("null" when there is none).</summary>
    public async Task<string?> RunAsync(string script)
    {
        if (_web == null || !_web.IsInitialized) return null;
        try { return await _web.ExecuteScriptAsync(script); }
        catch (Exception ex) { Log.Warn("page script: " + ex.Message); return null; }
    }

    /// <summary>A JSON string result ("\"text\"") as text.</summary>
    public static string JsonText(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return "";
        try { return JsonSerializer.Deserialize<string>(json) ?? ""; }
        catch { return json; }
    }

    public void FocusPage() => _web?.Focus();

    private void OnNavigationStarted(object? sender, NativeWebViewNavigationStartedEventArgs e)
    {
        var u = e.Uri;
        if (u == null) return;
        if (u.Scheme is "data" or "about") return;
        if (u.IsFile && _ownFolder.Length > 0 && u.LocalPath.StartsWith(_ownFolder, StringComparison.Ordinal)) return;
        e.Cancel = true;
        OpenOutside(u);
    }

    private void OpenOutside(Uri? u)
    {
        if (u == null) return;
        if (u.Scheme == "mailto") { MailtoClicked?.Invoke(u.OriginalString); return; }
        if (u.Scheme is "http" or "https") Shell.OpenWeb(u.AbsoluteUri);
    }

    public void DisposeView()
    {
        if (Focused == this) Focused = null;
        try { _web?.Dispose(); } catch { }
    }
}
