using System.Windows;
using System.Windows.Input;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class DeleteTelegramApiKeysDialog : Window
{
    public DeleteTelegramApiKeysDialog()
    {
        InitializeComponent();
        // Enter should only activate an explicitly focused action button.
        // Keep focus on a neutral element when the confirmation dialog opens.
        ContentRendered += (_, _) => Keyboard.Focus(DialogKeyboardFocusTarget);
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Delete_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

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
