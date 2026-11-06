namespace ChatPulse.Shared.Abstractions;

public interface ICommand;

public interface IValidationRequest;

public interface ICommandValidation : ICommand, IValidationRequest;