using FluentValidation;
using ChatPulse.Application.Services;
using ChatPulse.Shared.Abstractions;
using ChatPulse.Shared.Decorators;
using ChatPulse.Shared.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPulse.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddValidatorsFromAssembly(assembly);

        services.Scan(scan => scan.FromAssemblies(assembly)
            .AddClasses(classes => classes.AssignableToAny(
                typeof(ICommandHandler<,>),
                typeof(IQueryHandler<,>)))
            .AsSelfWithInterfaces()
            .WithScopedLifetime());

        services.AddScoped(typeof(ValidationExecutor<>));

        services.TryDecorate(
            typeof(ICommandHandler<,>),
            typeof(CommandValidationDecorator<,>));

        services.TryDecorate(
            typeof(IQueryHandler<,>),
            typeof(QueryValidationDecorator<,>));

        return services;
    }
}
