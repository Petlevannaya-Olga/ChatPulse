using CSharpFunctionalExtensions;
using FluentValidation;
using FluentValidation.Results;

namespace ChatPulse.Shared.Extensions;

public static class ValidationExtensions
{
    public static IRuleBuilderOptionsConditions<T, TProperty>
        MustBeValueObject<T, TProperty, TValueObject>(
            this IRuleBuilder<T, TProperty> ruleBuilder,
            Func<TProperty, Result<TValueObject, Error>> factory)
    {
        return ruleBuilder.Custom((value, context) =>
        {
            var result = factory(value);

            if (result.IsSuccess)
            {
                return;
            }

            context.AddFailure(new ValidationFailure(
                context.PropertyPath,
                result.Error.Message) { ErrorCode = result.Error.Code, CustomState = result.Error });
        });
    }

    public static IRuleBuilderOptionsConditions<T, TProperty?>
        MustBeValueObject<T, TProperty, TValueObject>(
            this IRuleBuilder<T, TProperty?> ruleBuilder,
            Func<TProperty, Result<TValueObject, Error>> factory)
        where TProperty : struct
    {
        return ruleBuilder.Custom((value, context) =>
        {
            if (!value.HasValue)
            {
                return;
            }

            var result = factory(value.Value);

            if (result.IsSuccess)
            {
                return;
            }

            context.AddFailure(new ValidationFailure(
                context.PropertyPath,
                result.Error.Message) { ErrorCode = result.Error.Code, CustomState = result.Error });
        });
    }
}