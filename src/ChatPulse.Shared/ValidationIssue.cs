namespace ChatPulse.Shared;

public sealed record ValidationIssue(
    string? Field,
    string Code,
    string Message);