using System.Text.Json;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Caching;
using Microsoft.Web.WebView2.Core;

namespace Magpie.App.Services;

/// <summary>One browser navigation at startup; later selections update a persistent reader shell.</summary>
public sealed class ReaderPresenter
{
    private CoreWebView2? _core;
    private bool _ready, _sending;
    private long _version, _sentVersion;
    private ReaderPage? _pending;
    private readonly LruCache<string, string> _browserPages = new(8, 32 * 1024 * 1024);
    private string _context = "";

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        core.NavigationCompleted += (_, e) =>
        {
            _ready = e.IsSuccess;
            _browserPages.Clear();
            if (_ready) _ = FlushAsync();
            else Log.Warn("reader shell could not be loaded: " + e.WebErrorStatus);
        };
        core.Navigate(WebHost.Publish(ReaderShell.Build(ThemeManager.IsDark), "reader-shell"));
    }

    public void Show(ReaderPage page)
    {
        _pending = page;
        _version++;
        _ = FlushAsync();
    }

    public void Loading(string bodyHtml) => Show(new ReaderPage("loading", "", "<body>" + bodyHtml + "</body>", 0, Retain: false));

    private async Task FlushAsync()
    {
        if (!_ready || _sending || _core == null) return;
        _sending = true;
        try
        {
            while (_sentVersion != _version && _pending is { } page)
            {
                var version = _version;
                if (page.Context.Length > 0 && page.Context != _context)
                {
                    _browserPages.Clear();
                    _context = page.Context;
                }
                var reuse = page.Retain && _browserPages.TryGet(page.Key, out var fp) && fp == page.Fingerprint;
                string Payload(bool cached) => JsonSerializer.Serialize(new
                {
                    key = page.Key, fingerprint = page.Fingerprint, body = cached ? "" : page.Body, style = cached ? "" : page.Style,
                    bytes = page.Bytes, context = page.Context, retain = page.Retain, version, reuse = cached,
                    read = page.ReadStates,
                    hoverDelay = Math.Clamp(AppServices.Engine.Config.Appearance.FolderHover.DelayMs, 50, 1000),
                });
                var result = await _core.ExecuteScriptAsync("magpiePresent(" + Payload(reuse) + ")");
                if (result == "false" && reuse)
                    await _core.ExecuteScriptAsync("magpiePresent(" + Payload(false) + ")");
                if (page.Retain) _browserPages.Set(page.Key, page.Fingerprint, page.Bytes);
                _sentVersion = version;
            }
        }
        catch (Exception ex) { Log.Warn("show conversation: " + ex.Message); }
        finally { _sending = false; }
    }
}
