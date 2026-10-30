namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// A time-change request that can't proceed (bad input, unknown draft, wrong token…).
/// The message is written for the agent and the user; <see cref="Code"/> is for tests and the card.
/// </summary>
public sealed class TimeChangeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
