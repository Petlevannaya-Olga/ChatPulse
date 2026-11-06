namespace ChatPulse.Shared;

public static class CommonErrors
{
    public static Error Validation(
        string code,
        string message,
        string? field = null)
    {
        return new Error(
            code,
            message,
            ErrorType.VALIDATION,
            field);
    }

    public static Error Validation(IReadOnlyList<ValidationIssue> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            throw new ArgumentException(
                "Коллекция ошибок валидации не может быть пустой",
                nameof(errors));
        }

        return new Error(
            "validation.failed",
            "Некорректные данные",
            ErrorType.VALIDATION,
            Errors: errors);
    }

    public static Error Conflict(
        string code,
        string message,
        string? field = null)
    {
        return new Error(
            code,
            message,
            ErrorType.CONFLICT,
            field);
    }

    public static Error NotFound(
        string code,
        string message)
    {
        return new Error(
            code,
            message,
            ErrorType.NOT_FOUND);
    }

    public static Error Unauthorized(
        string code,
        string message)
    {
        return new Error(
            code,
            message,
            ErrorType.UNAUTHORIZED);
    }
}