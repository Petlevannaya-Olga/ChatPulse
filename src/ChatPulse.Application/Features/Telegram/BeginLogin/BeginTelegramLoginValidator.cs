using FluentValidation;

namespace ChatPulse.Application.Features.Telegram.BeginLogin;

public sealed class BeginTelegramLoginValidator : AbstractValidator<BeginTelegramLoginCommand>
{
    public BeginTelegramLoginValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Введите номер телефона.")
            .Matches(@"^\+7[0-9]{10}$")
            .WithMessage("Введите 10 цифр номера после +7.");
    }
}
