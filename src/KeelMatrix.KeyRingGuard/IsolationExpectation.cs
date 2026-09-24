namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Describes the isolation contract expected by a scenario.
/// </summary>
public enum IsolationExpectation
{
    /// <summary>
    /// A payload from the other logical boundary must not be readable.
    /// </summary>
    RejectCrossUnprotect = 0
}
