using System.Windows;
using System.Windows.Input;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class TelegramLoginInProgressDialog : Window
{
    public TelegramLoginInProgressDialog()
    {
        InitializeComponent();
        ModalDialogKeyboardPolicy.Apply(this, DialogKeyboardFocusTarget);
    }

    private void KeepLoginButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void CancelLoginButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }
}
