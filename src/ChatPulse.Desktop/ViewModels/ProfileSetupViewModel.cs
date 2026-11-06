using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChatPulse.Desktop.ViewModels;

/// <summary>
/// Состояние первого шага онбординга. Пока хранится в памяти окна;
/// постоянное сохранение профиля добавим вместе с завершением онбординга.
/// </summary>
public sealed class ProfileSetupViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNameValid));
            OnPropertyChanged(nameof(IsNameInvalid));
            OnPropertyChanged(nameof(IsNameEmpty));
            OnPropertyChanged(nameof(AvatarInitial));
            OnPropertyChanged(nameof(ValidationMessage));
        }
    }

    public bool IsNameValid => Name.Trim().Length is >= 2 and <= 50;

    public bool IsNameEmpty => string.IsNullOrWhiteSpace(Name);

    public bool IsNameInvalid => !IsNameEmpty && !IsNameValid;

    public string AvatarInitial => IsNameEmpty ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();

    public string ValidationMessage => IsNameInvalid
        ? "Введите имя длиной от 2 до 50 символов."
        : string.Empty;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
