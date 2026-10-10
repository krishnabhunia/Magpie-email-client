using Magpie.Core;
using Magpie.Core.Caching;
using Magpie.Core.Mail;

namespace Magpie.Mac.Services;

/// <summary>
/// One page load at start (Core's reader shell); every conversation after that is handed to the shell's
/// magpiePresent() — the same way the Windows reader works, so switching conversations doesn't reload the page.
/// </summary>
public sealed class ReaderPresenter
{
    private readonly WebSurface _web;
    private readonly Func<int> _hoverDelay;
    private bool _ready, _sending;
    private long _version, _sentVersion;
    private ReaderPage? _pending;
    private readonly LruCache<string, string> _browserPages = new(8, 32 * 1024 * 1024);
    private string _context = "";

    public ReaderPresenter(WebSurface web, Func<int> hoverDelay)
    {
        _web = web;
        _hoverDelay = hoverDelay;
        web.PageLoaded += ok =>
        {
            _ready = ok;
            _browserPages.Clear();
            _sentVersion = 0;
            if (ok) _ = FlushAsync();
        };
    }

    /// <summary>(Re)loads the shell, e.g. at start and when the Mac switches between light and dark.</summary>
    public void LoadShell(bool dark)
    {
        _ready = false;
        _web.Load(ReaderShell.Build(dark), "reader-shell");
    }

    public void Show(ReaderPage page)
    {
        _pending = page;
        _version++;
        _ = FlushAsync();
    }

    private async Task FlushAsync()
    {
        if (!_ready || _sending) return;
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
                var result = await _web.RunAsync(ReaderShell.PresentScript(page, version, reuse, _hoverDelay()));
                if (result == "false" && reuse)
                    await _web.RunAsync(ReaderShell.PresentScript(page, version, false, _hoverDelay()));
                if (page.Retain) _browserPages.Set(page.Key, page.Fingerprint, page.Bytes);
                _sentVersion = version;
            }
        }
        catch (Exception ex) { Log.Warn("show conversation: " + ex.Message); }
        finally { _sending = false; }
    }
}
