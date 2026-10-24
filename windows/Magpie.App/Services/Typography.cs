using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Magpie.Core.Settings;

namespace Magpie.App.Services;

/// <summary>Semantic text roles. Only explicit saved overrides replace an existing WPF text style.</summary>
public static class Typography
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(string), typeof(Typography), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits, RoleChanged));
    public static void SetRole(DependencyObject d, string value) => d.SetValue(RoleProperty, value);
    public static string GetRole(DependencyObject d) => (string)d.GetValue(RoleProperty);

    /// <summary>Font samples must show their own staged row, rather than receive a saved UI override.</summary>
    public static readonly DependencyProperty IsSampleProperty = DependencyProperty.RegisterAttached(
        "IsSample", typeof(bool), typeof(Typography), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetIsSample(DependencyObject d, bool value) => d.SetValue(IsSampleProperty, value);
    public static bool GetIsSample(DependencyObject d) => (bool)d.GetValue(IsSampleProperty);

    private sealed class Original
    {
        public required DependencyProperty FamilyProperty;
        public required DependencyProperty SizeProperty;
        public object Family = DependencyProperty.UnsetValue;
        public object Size = DependencyProperty.UnsetValue;
        public BindingBase? FamilyBinding;
        public BindingBase? SizeBinding;
        public string Role = "";
        public bool Applied;
        public readonly Dictionary<DependencyProperty, (object Value, BindingBase? Binding)> Layout = new();
    }

    private static readonly ConditionalWeakTable<DependencyObject, Original> Originals = new();
    private static readonly List<WeakReference<FrameworkElement>> Seen = new();
    private static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Installed = new(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
    private static TypographySettings _settings = new();
    private static bool _started;

    public static void Start(TypographySettings settings)
    {
        if (!_started)
        {
            _started = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(Loaded), true);
        }
        Apply(settings);
    }

    public static void Apply(TypographySettings settings)
    {
        _settings = settings.Clone();
        _settings.Normalise();
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => Apply(_settings)), DispatcherPriority.Loaded);
            return;
        }
        for (var i = Seen.Count - 1; i >= 0; i--)
        {
            if (Seen[i].TryGetTarget(out var element)) ApplyElement(element);
            else Seen.RemoveAt(i);
        }
        if (Application.Current != null)
            foreach (Window window in Application.Current.Windows) ApplyWindow(window);
    }

    public static void ApplyWindow(Window window) => Visit(window);

    private static void Visit(DependencyObject parent)
    {
        if (parent is FrameworkElement element) ApplyElement(element);
        if (parent is Visual || parent is System.Windows.Media.Media3D.Visual3D)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) Visit(VisualTreeHelper.GetChild(parent, i));
    }

    private static void Loaded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element) return;
        ApplyElement(element);
    }

    private static void RoleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (_started && d is FrameworkElement element && element.IsLoaded) ApplyElement(element);
    }

    public static FontFamily Font(string family)
    {
        if (Families.TryGetValue(family, out var cached)) return cached;
        FontFamily result;
        if (family.Equals("IBM Plex Sans", StringComparison.OrdinalIgnoreCase))
            result = new FontFamily("pack://application:,,,/Assets/Fonts/#IBM Plex Sans, Segoe UI");
        else if (family.Equals("IBM Plex Serif", StringComparison.OrdinalIgnoreCase))
            result = new FontFamily("pack://application:,,,/Assets/Fonts/#IBM Plex Serif, Georgia");
        else if (Installed.Contains(family)) result = new FontFamily(family);
        else result = new FontFamily("Segoe UI");
        Families[family] = result;
        return result;
    }
    public static FontFamily ResolveFont(string family) => Font(family);

    private static bool IsGlyph(FrameworkElement element)
    {
        var family = element switch { TextBlock text => text.FontFamily.Source, Control control => control.FontFamily.Source, _ => "" };
        if (family.Contains("Fluent Icons", StringComparison.OrdinalIgnoreCase) || family.Contains("MDL2", StringComparison.OrdinalIgnoreCase)) return true;
        var textValue = element switch { TextBlock text => text.Text, ContentControl control => control.Content as string, _ => null };
        return !string.IsNullOrEmpty(textValue) && textValue.All(c => char.IsWhiteSpace(c) || c is >= '\uE000' and <= '\uF8FF');
    }

    private static void ApplyElement(FrameworkElement element)
    {
        var known = Originals.TryGetValue(element, out var original);
        if (GetIsSample(element) || (!known && IsGlyph(element))) return;
        DependencyProperty familyProperty, sizeProperty;
        if (element is TextBlock) { familyProperty = TextBlock.FontFamilyProperty; sizeProperty = TextBlock.FontSizeProperty; }
        else if (element is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or MenuItem or ButtonBase or Label)
        { familyProperty = Control.FontFamilyProperty; sizeProperty = Control.FontSizeProperty; }
        else return;

        var role = GetRole(element);
        if (string.IsNullOrEmpty(role)) role = known ? original!.Role : InferRole(element);
        if (TypographyCatalog.Find(role) == null) return;

        if (!known)
        {
            original = new Original
            {
                FamilyProperty = familyProperty, SizeProperty = sizeProperty,
                Family = element.ReadLocalValue(familyProperty), Size = element.ReadLocalValue(sizeProperty),
                FamilyBinding = BindingOperations.GetBindingBase(element, familyProperty),
                SizeBinding = BindingOperations.GetBindingBase(element, sizeProperty),
                Role = role,
            };
            Originals.Add(element, original);
            Seen.Add(new WeakReference<FrameworkElement>(element));
        }

        if (_settings.HasOverride(role))
        {
            var style = _settings.Resolve(role);
            RestoreLayout(element, original!);
            element.SetCurrentValue(familyProperty, Font(style.Family));
            element.SetCurrentValue(sizeProperty, style.Size);
            if (style.Size >= 24)
            {
                // A large saved font must not stay inside a fixed small text field or single-line label.
                if (element is TextBlock)
                {
                    SetLayout(element, original!, TextBlock.TextWrappingProperty, TextWrapping.Wrap);
                    SetLayout(element, original!, TextBlock.LineHeightProperty, double.NaN);
                }
                if (!double.IsNaN(element.Height) && element.Height < style.Size * 1.5)
                    SetLayout(element, original!, FrameworkElement.HeightProperty, double.NaN);
            }
            original!.Applied = true;
        }
        else if (original!.Applied)
        {
            Restore(element, familyProperty, original.Family, original.FamilyBinding);
            Restore(element, sizeProperty, original.Size, original.SizeBinding);
            RestoreLayout(element, original);
            original.Applied = false;
        }
        if (element is TextBlock block)
            foreach (var inline in block.Inlines) ApplyInline(inline, role);
    }

    private static void ApplyInline(Inline inline, string parentRole)
    {
        var family = inline.FontFamily.Source;
        if (family.Contains("Fluent Icons", StringComparison.OrdinalIgnoreCase) || family.Contains("MDL2", StringComparison.OrdinalIgnoreCase)) return;
        if (inline is Run run && !string.IsNullOrEmpty(run.Text) && run.Text.All(c => char.IsWhiteSpace(c) || c is >= '\uE000' and <= '\uF8FF')) return;
        var role = GetRole(inline);
        if (string.IsNullOrEmpty(role)) role = parentRole;
        if (TypographyCatalog.Find(role) == null) return;
        if (!Originals.TryGetValue(inline, out var original))
        {
            original = new Original
            {
                FamilyProperty = TextElement.FontFamilyProperty, SizeProperty = TextElement.FontSizeProperty,
                Family = inline.ReadLocalValue(TextElement.FontFamilyProperty), Size = inline.ReadLocalValue(TextElement.FontSizeProperty),
                FamilyBinding = BindingOperations.GetBindingBase(inline, TextElement.FontFamilyProperty),
                SizeBinding = BindingOperations.GetBindingBase(inline, TextElement.FontSizeProperty), Role = role,
            };
            Originals.Add(inline, original);
        }
        if (_settings.HasOverride(role))
        {
            var style = _settings.Resolve(role);
            inline.SetCurrentValue(TextElement.FontFamilyProperty, Font(style.Family));
            inline.SetCurrentValue(TextElement.FontSizeProperty, style.Size);
            original.Applied = true;
        }
        else if (original.Applied)
        {
            Restore(inline, TextElement.FontFamilyProperty, original.Family, original.FamilyBinding);
            Restore(inline, TextElement.FontSizeProperty, original.Size, original.SizeBinding);
            original.Applied = false;
        }
        if (inline is Span span)
            foreach (var child in span.Inlines) ApplyInline(child, role);
    }

    private static void SetLayout(FrameworkElement element, Original original, DependencyProperty property, object value)
    {
        if (!original.Layout.ContainsKey(property))
            original.Layout[property] = (element.ReadLocalValue(property), BindingOperations.GetBindingBase(element, property));
        element.SetCurrentValue(property, value);
    }

    private static void RestoreLayout(FrameworkElement element, Original original)
    {
        foreach (var (property, value) in original.Layout) Restore(element, property, value.Value, value.Binding);
    }

    private static void Restore(DependencyObject element, DependencyProperty property, object value, BindingBase? binding)
    {
        if (binding != null) BindingOperations.SetBinding(element, property, binding);
        else if (value == DependencyProperty.UnsetValue) element.ClearValue(property);
        else element.SetValue(property, value);
    }

    private static string InferRole(FrameworkElement element)
    {
        if (element is MenuItem || element.TemplatedParent is MenuItem) return "menu";
        if (element is TextBlock && Window.GetWindow(element)?.GetType().Name == "CommandPaletteWindow") return "menu";
        if (element is Label) return "fieldLabel";
        if (element is TextBoxBase || element is PasswordBox || element is ComboBox || element is ComboBoxItem) return "fieldValue";
        if (element is ButtonBase || element.TemplatedParent is ButtonBase) return "toolbar";
        if (element is TextBlock text)
        {
            var path = BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path ?? "";
            if (path.Contains("AiPreview", StringComparison.OrdinalIgnoreCase)) return "aiDraft";
            if (path.Contains("SummaryText", StringComparison.OrdinalIgnoreCase)) return "aiSummary";
            if (path.Contains("AiError", StringComparison.OrdinalIgnoreCase) || path.Contains("Error", StringComparison.OrdinalIgnoreCase)) return "validation";
            if (path.Contains("Status", StringComparison.OrdinalIgnoreCase)) return "aiStatus";
            if (Window.GetWindow(element)?.GetType().Name == "SettingsWindow")
            {
                if (text.FontSize >= 19) return "settingsTitle";
                if (text.Text.All(c => !char.IsLetter(c) || char.IsUpper(c)) && text.Text.Any(char.IsLetter)) return "settingsSection";
                return text.FontSize <= 12.5 && text.FontWeight == FontWeights.Normal ? "helper" : "fieldLabel";
            }
            if (Window.GetWindow(element)?.GetType().Name.EndsWith("Dialog", StringComparison.Ordinal) == true)
                return text.FontSize >= 17 ? "dialogTitle" : "dialogBody";
            if (text.FontSize >= 19) return "dialogTitle";
            return text.FontSize <= 12.5 ? "helper" : "dialogBody";
        }
        return "fieldValue";
    }
}
