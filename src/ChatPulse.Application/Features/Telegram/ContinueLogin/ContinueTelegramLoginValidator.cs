using FluentValidation;

namespace ChatPulse.Application.Features.Telegram.ContinueLogin;

public sealed class ContinueTelegramLoginValidator : AbstractValidator<ContinueTelegramLoginCommand>
{
    public ContinueTelegramLoginValidator()
    {
        RuleFor(x => x.Value)
            .NotEmpty().WithMessage("Заполните поле подтверждения.")
            .MaximumLength(256).WithMessage("Значение слишком длинное.");
    }
}
