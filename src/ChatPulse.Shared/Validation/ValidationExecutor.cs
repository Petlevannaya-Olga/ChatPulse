using System.Text.Json;
using CSharpFunctionalExtensions;
using FluentValidation;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Shared.Validation;

public sealed class ValidationExecutor<TRequest>(IEnumerable<IValidator<TRequest>> validators)
{
    public async Task<Result<TResponse, Error>> ExecuteAsync<TResponse>(
        TRequest request,
        Func<CancellationToken, Task<Result<TResponse, Error>>> next,
        CancellationToken cancellationToken)
    {
        if (request is not IValidationRequest)
        {
            return await next(cancellationToken);
        }

        var registeredValidators = validators.ToArray();

        if (registeredValidators.Length == 0)
        {
            throw new InvalidOperationException(
                $"Не найден валидатор для {typeof(TRequest).Name}");
        }

        var issues = new List<ValidationIssue>();

        foreach (var validator in registeredValidators)
        {
            var result = await validator.ValidateAsync(
                request,
                cancellationToken);

            foreach (var failure in result.Errors)
            {
                var code = failure.CustomState is Error domainError
                    ? domainError.Code
                    : "validation.failed";

                var field = string.IsNullOrWhiteSpace(failure.PropertyName)
                    ? null
                    : JsonNamingPolicy.SnakeCaseLower.ConvertName(
                        failure.PropertyName);

                issues.Add(new ValidationIssue(
                    field,
                    code,
                    failure.ErrorMessage));
            }
        }

        if (issues.Count > 0)
        {
            return CommonErrors.Validation(issues);
        }

        return await next(cancellationToken);
    }
}