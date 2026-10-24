using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

/// <summary>A staged row. Invalid text remains editable and cannot be saved.</summary>
public sealed class EditableTextRole : ObservableObject
{
    public TextRole Role { get; }
    public string Id => Role.Id;
    public string Name => Role.Name;
    public string Area => Role.Area;
    public string Sample => Role.Sample;
    private string _family;
    private string _sizeText;
    private bool _isOverride;
    private bool _loading;
    public event Action<EditableTextRole>? Edited;
    public EditableTextRole(TextRole role, TypographySettings settings)
    {
        Role = role;
        var style = settings.Resolve(role.Id);
        _family = style.Family;
        _sizeText = style.Size.ToString("0.#", CultureInfo.InvariantCulture);
        _isOverride = settings.HasOverride(role.Id);
    }
    public string Family
    {
        get => _family;
        set { if (SetProperty(ref _family, value ?? "")) Changed(); }
    }
    public string SizeText
    {
        get => _sizeText;
        set { if (SetProperty(ref _sizeText, value ?? "")) Changed(); }
    }
    public bool IsOverride => _isOverride;
    public bool IsValid => TypographyCatalog.IsValidFamily(Family) && ParsedSize is { } n && TypographyCatalog.IsValidSize(n);
    public double? ParsedSize => double.TryParse(SizeText, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)
        || double.TryParse(SizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    public double PreviewSize => IsValid ? ParsedSize!.Value : Role.DefaultSize;
    public FontFamily PreviewFamily => Typography.Font(TypographyCatalog.IsValidFamily(Family) ? Family.Trim() : Role.DefaultFamily);
    public string Points => IsValid ? (PreviewSize * .75).ToString("0.##", CultureInfo.CurrentCulture) + " pt" : "Invalid size";
    public string State => IsOverride ? "Custom" : "Built-in";
    public TextStyle ToStyle() => new() { Family = Family.Trim(), Size = ParsedSize ?? Role.DefaultSize };
    private void Changed()
    {
        if (!_loading) _isOverride = true;
        foreach (var name in new[] { nameof(IsOverride), nameof(IsValid), nameof(PreviewSize), nameof(PreviewFamily), nameof(Points), nameof(State) })
            OnPropertyChanged(name);
        if (!_loading) Edited?.Invoke(this);
    }
    public void Reset()
    {
        _loading = true;
        Family = Role.DefaultFamily;
        SizeText = Role.DefaultSize.ToString("0.#", CultureInfo.InvariantCulture);
        _loading = false;
        _isOverride = false;
        foreach (var name in new[] { nameof(IsOverride), nameof(IsValid), nameof(PreviewSize), nameof(PreviewFamily), nameof(Points), nameof(State) })
            OnPropertyChanged(name);
        Edited?.Invoke(this);
    }
}

public partial class SettingsViewModel
{
    public ObservableCollection<EditableTextRole> TextRoles { get; } = new();
    public ObservableCollection<string> TextFontFamilies { get; } = new();
    public string[] TextAreas { get; } = new[] { "All text areas" }.Concat(TypographyCatalog.Roles.Select(r => r.Area).Distinct()).ToArray();
    public ICollectionView TextRoleView => CollectionViewSource.GetDefaultView(TextRoles);
    private string _textArea = "Inbox list", _textRoleSearch = "";
    private EditableTextRole? _selectedTextRole;
    private bool _useReadingFonts;
    public string TextArea
    {
        get => _textArea;
        set { if (SetProperty(ref _textArea, value)) { RefreshTextRoles(); OnPropertyChanged(nameof(TextRoleCount)); } }
    }
    public string TextRoleSearch
    {
        get => _textRoleSearch;
        set { if (SetProperty(ref _textRoleSearch, value)) { RefreshTextRoles(); OnPropertyChanged(nameof(TextRoleCount)); } }
    }
    public EditableTextRole? SelectedTextRole
    {
        get => _selectedTextRole;
        set => SetProperty(ref _selectedTextRole, value);
    }
    public bool UseReadingFonts
    {
        get => _useReadingFonts;
        set { if (SetProperty(ref _useReadingFonts, value)) NoteChange(); }
    }
    public bool HasTypographyErrors => TextRoles.Any(r => !r.IsValid);
    public string TypographyValidationMessage => HasTypographyErrors
        ? "Enter a valid font name and a size from 8 to 72 px, in steps of 0.5. Apply is unavailable until every row is valid."
        : "";
    public string TextRoleCount => TextRoleView.Cast<object>().Count() + " of " + TextRoles.Count + " text roles";

    public void LoadTypography(TypographySettings settings)
    {
        TextRoles.Clear();
        foreach (var definition in TypographyCatalog.Roles)
        {
            var row = new EditableTextRole(definition, settings);
            row.Edited += OnTextRoleEdited;
            TextRoles.Add(row);
        }
        _useReadingFonts = settings.OverrideReceivedFonts;
        OnPropertyChanged(nameof(UseReadingFonts));
        TextFontFamilies.Clear();
        var names = Fonts.SystemFontFamilies.Select(f => f.Source).Concat(new[] { "IBM Plex Sans", "IBM Plex Serif" })
            .Concat(settings.Styles.Values.Select(s => s.Family)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase);
        foreach (var name in names) TextFontFamilies.Add(name);
        TextRoleView.Filter = item => item is EditableTextRole row && MatchesTextRole(row);
        SelectedTextRole = TextRoles.FirstOrDefault(r => r.Id == "listSubject");
        RefreshTextRoles();
    }

    public TypographySettings TypographyToSave()
    {
        var result = new TypographySettings { OverrideReceivedFonts = UseReadingFonts };
        foreach (var row in TextRoles.Where(r => r.IsOverride && r.IsValid)) result.Styles[row.Id] = row.ToStyle();
        return result;
    }

    private bool MatchesTextRole(EditableTextRole row) => !string.IsNullOrWhiteSpace(TextRoleSearch)
        ? (row.Name + " " + row.Area + " " + row.Sample).Contains(TextRoleSearch.Trim(), StringComparison.CurrentCultureIgnoreCase)
        : TextArea == "All text areas" || row.Area == TextArea;

    private void RefreshTextRoles()
    {
        TextRoleView.Refresh();
        if (SelectedTextRole == null || !MatchesTextRole(SelectedTextRole))
            SelectedTextRole = TextRoleView.Cast<EditableTextRole>().FirstOrDefault();
        OnPropertyChanged(nameof(TextRoleCount));
    }

    private void OnTextRoleEdited(EditableTextRole row)
    {
        SelectedTextRole = row;
        OnPropertyChanged(nameof(HasTypographyErrors));
        OnPropertyChanged(nameof(TypographyValidationMessage));
        OnPropertyChanged(nameof(CanApplySettings));
        NoteChange();
    }

    [RelayCommand] private void ResetTextRole(EditableTextRole? row) => row?.Reset();
    [RelayCommand] private void ResetTextArea()
    {
        foreach (var row in TextRoles.Where(r => TextArea == "All text areas" || r.Area == TextArea)) row.Reset();
    }
    [RelayCommand] private void ResetAllTextStyles()
    {
        foreach (var row in TextRoles) row.Reset();
        UseReadingFonts = false;
    }
}
