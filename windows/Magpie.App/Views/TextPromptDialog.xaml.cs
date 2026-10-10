using System.Windows;

namespace Magpie.App.Views;

public partial class TextPromptDialog : Window
{
    public TextPromptDialog() { InitializeComponent(); }

    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var d = new TextPromptDialog { Owner = owner };
        d.Heading.Text = title;
        d.LabelText.Text = label;
        d.Input.Text = initial;
        d.Loaded += (_, _) => { d.Input.Focus(); d.Input.CaretIndex = d.Input.Text.Length; };
        return d.ShowDialog() == true ? d.Input.Text.Trim() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
