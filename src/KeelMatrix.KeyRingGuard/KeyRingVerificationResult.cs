namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Contains the bounded, non-sensitive outcome of a verification.
/// </summary>
public sealed class KeyRingVerificationResult
{
    internal KeyRingVerificationResult(
        KeyRingScenario scenario,
        bool succeeded,
        KeyRingFailureKind failureKind,
        string message,
        TimeSpan duration)
    {
        Scenario = scenario;
        Succeeded = succeeded;
        FailureKind = failureKind;
        Message = message;
        Duration = duration;
    }

    /// <summary>
    /// Gets the scenario that produced this result.
    /// </summary>
    public KeyRingScenario Scenario { get; }

    /// <summary>
    /// Gets a value indicating whether the scenario's contract was proven.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the bounded failure category, or <see cref="KeyRingFailureKind.None"/> on success.
    /// </summary>
    public KeyRingFailureKind FailureKind { get; }

    /// <summary>
    /// Gets a non-sensitive human-readable result message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the elapsed verification time.
    /// </summary>
    public TimeSpan Duration { get; }
}
