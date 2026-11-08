using System.Globalization;
using FluentValidation;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Save;

public sealed class SaveTelegramApiCredentialsValidator : AbstractValidator<SaveTelegramApiCredentialsCommand>
{
    public SaveTelegramApiCredentialsValidator()
    {
        RuleFor(x => x.ApiId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Введите API ID.")
            .Must(value => int.TryParse(value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) && id > 0)
            .WithMessage("API ID должен быть положительным целым числом.");

        RuleFor(x => x.ApiHash)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Введите API Hash.")
            .Matches(@"\A[0-9a-fA-F]{32}\z")
            .WithMessage("API Hash должен содержать 32 шестнадцатеричных символа.");
    }
}
