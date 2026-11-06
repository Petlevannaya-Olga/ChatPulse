using System.Text.Json.Serialization;

namespace ChatPulse.Shared;

public sealed record Error(
    string Code,
    string Message,
    ErrorType Type,
    string? Field = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ValidationIssue>? Errors = null);