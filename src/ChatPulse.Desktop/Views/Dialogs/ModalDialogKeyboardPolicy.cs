using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ChatPulse.Desktop.Views.Dialogs;

/// <summary>
/// Keeps Enter from invoking implicit default actions in ChatPulse modal dialogs.
/// Enter activates only the button explicitly focused inside the active dialog.
/// </summary>
internal static class ModalDialogKeyboardPolicy
{
    public static void Apply(Window dialog, FrameworkElement neutralFocusTarget)
    {
        dialog.ContentRendered += (_, _) => Keyboard.Focus(neutralFocusTarget);
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;

            // Prevent the WPF default-button mechanism and unintentional repeats.
            e.Handled = true;
            if (e.IsRepeat) return;

            if (Keyboard.FocusedElement is not Button button ||
                !button.IsKeyboardFocused || !button.IsEnabled || !button.IsVisible ||
                Window.GetWindow(button) != dialog)
                return;

            // These custom dialogs use Click handlers (no Commands).
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        };
    }
}
