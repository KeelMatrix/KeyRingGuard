namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Describes the continuity contract expected by a scenario.
/// </summary>
public enum ContinuityExpectation
{
    /// <summary>
    /// A payload protected before the provider transition must remain readable.
    /// </summary>
    PreserveProtectedPayload = 0
}
