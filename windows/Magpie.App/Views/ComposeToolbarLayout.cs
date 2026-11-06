using System.Windows;
using Magpie.App.Services;
using Magpie.Core.Settings;

namespace Magpie.App.Views;

public partial class ComposeWindow
{
    private readonly Dictionary<string, FrameworkElement> _composeToolbarControls = new(StringComparer.Ordinal);
    private string? _composeToolbarLayout;

    private void InitializeComposeToolbar()
    {
        foreach (var control in FormattingItems.Items.OfType<FrameworkElement>())
        {
            var id = ComposeToolbarHost.GetId(control);
            if (id.Length > 0) _composeToolbarControls.Add(id, control);
        }
        ApplyComposeToolbarLayout();
    }

    private void ApplyComposeToolbarLayout()
    {
        var buttons = ComposeToolbarCatalog.Normalise(AppServices.Engine.Config.Appearance.ComposeToolbar);
        var signature = string.Join("|", buttons.Select(button => button.Id + ":" + button.Visible));
        // Settings also changes for unrelated preferences: do not steal focus/reparent the active toolbar then.
        if (signature == _composeToolbarLayout) return;
        ComposeToolbarHost.Apply(FormattingItems, _composeToolbarControls, buttons);
        _composeToolbarLayout = signature;
        UpdatePlainTextControls();
    }
}
