using System.Windows.Controls;

namespace Magpie.App.Views;

public partial class AiReplyDraftView : UserControl
{
    public AiReplyDraftView() => InitializeComponent();

    /// <summary>AW1: "Yes, if…" / "Custom" put the cursor at the end of "What the reply says".</summary>
    public void FocusAnswer()
    {
        AnswerBox.Focus();
        AnswerBox.CaretIndex = AnswerBox.Text.Length;
    }

    /// <summary>True while the keyboard is in this panel (Ctrl+Enter then rewrites instead of sending the email).</summary>
    public bool HasKeyboardFocus => IsKeyboardFocusWithin;
}
