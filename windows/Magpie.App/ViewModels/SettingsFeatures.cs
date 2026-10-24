namespace Magpie.App.ViewModels;

public partial class SettingsViewModel
{
    /// <summary>Invalid staged font input cannot be saved by the buttons or Enter.</summary>
    public bool CanApplySettings => HasChanges && !HasTypographyErrors;

    partial void OnHasChangesChanged(bool value) => OnPropertyChanged(nameof(CanApplySettings));
}
