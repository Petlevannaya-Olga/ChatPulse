namespace ChatPulse.Application.Services;

public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}
