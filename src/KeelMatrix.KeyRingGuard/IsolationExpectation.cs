namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Describes the isolation contract expected by a scenario.
/// </summary>
public enum IsolationExpectation
{
    /// <summary>
    /// A payload from the other logical boundary must be rejected by the standard ASP.NET Core
    /// persisted-protector contract rather than by an arbitrary provider exception.
    /// </summary>
    RejectCrossUnprotect = 0
}
