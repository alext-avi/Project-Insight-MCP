namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// A time-change request that can't proceed (bad input, unknown draft, wrong token…).
/// The message is written for the agent and the user; <see cref="Code"/> is for tests and the card. An inner
/// exception is for logs and debugging only: callers show <see cref="Exception.Message"/>, never the inner one.
/// </summary>
public sealed class TimeChangeException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
