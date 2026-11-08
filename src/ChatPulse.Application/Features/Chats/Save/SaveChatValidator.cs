using FluentValidation;

namespace ChatPulse.Application.Features.Chats.Save;

public sealed class SaveChatValidator : AbstractValidator<SaveChatCommand>
{
    public SaveChatValidator()
    {
        RuleFor(x => x.PeerId)
            .GreaterThan(0).WithMessage("Не выбран чат Telegram.");
        RuleFor(x => x.PeerType)
            .Must(value => value is "Chat" or "Channel")
            .WithMessage("Неизвестный тип Telegram-чата.");
        RuleFor(x => x.TelegramTitle)
            .NotEmpty().MaximumLength(255);
        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("Введите название чата.")
            .MaximumLength(100).WithMessage("Название чата слишком длинное.");
        RuleFor(x => x.SocialNetwork)
            .Must(value => value is "Instagram" or "Telegram" or "TikTok")
            .WithMessage("Выберите соцсеть заданий.");
        RuleFor(x => x.StartDate)
            .NotEqual(default(DateOnly)).WithMessage("Укажите дату начала активности.");
    }
}
