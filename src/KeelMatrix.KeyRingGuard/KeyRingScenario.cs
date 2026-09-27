namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// An immutable description of a supported Data Protection transition to verify.
/// </summary>
public sealed class KeyRingScenario
{
    private enum ScenarioKind
    {
        RestartContinuity,
        ReplicaSharing,
        ApplicationIsolation,
        PurposeIsolation,
        RotationContinuity
    }

    private KeyRingScenario(
        string name,
        ScenarioKind kind,
        ContinuityExpectation? continuityExpectation = null,
        IsolationExpectation? isolationExpectation = null)
    {
        Name = name;
        Kind = kind;
        Continuity = continuityExpectation;
        Isolation = isolationExpectation;
    }

    /// <summary>
    /// Verifies a payload across independently recreated providers using one factory.
    /// </summary>
    public static KeyRingScenario RestartContinuity { get; } = new(
        "Restart continuity",
        ScenarioKind.RestartContinuity,
        ContinuityExpectation.PreserveProtectedPayload);

    /// <summary>
    /// Verifies payload exchange in both directions between two independently created providers.
    /// </summary>
    public static KeyRingScenario ReplicaSharing { get; } = new(
        "Replica sharing",
        ScenarioKind.ReplicaSharing,
        ContinuityExpectation.PreserveProtectedPayload);

    /// <summary>
    /// Verifies that different application discriminators reject one another's payloads after a same-boundary control.
    /// </summary>
    public static KeyRingScenario ApplicationIsolation { get; } = new(
        "Application isolation",
        ScenarioKind.ApplicationIsolation,
        isolationExpectation: IsolationExpectation.RejectCrossUnprotect);

    /// <summary>
    /// Verifies that different purposes reject one another's payloads after a same-purpose exchange control.
    /// </summary>
    public static KeyRingScenario PurposeIsolation { get; } = new(
        "Purpose isolation",
        ScenarioKind.PurposeIsolation,
        isolationExpectation: IsolationExpectation.RejectCrossUnprotect);

    /// <summary>
    /// Verifies an observed active key transition while preserving payloads from before and after rotation.
    /// </summary>
    public static KeyRingScenario RotationContinuity { get; } = new(
        "Rotation continuity",
        ScenarioKind.RotationContinuity,
        ContinuityExpectation.PreserveProtectedPayload);

    /// <summary>
    /// Gets the user-facing name of the scenario.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the continuity expectation, when the scenario tests continuity.
    /// </summary>
    public ContinuityExpectation? Continuity { get; }

    /// <summary>
    /// Gets the isolation expectation, when the scenario tests isolation.
    /// </summary>
    public IsolationExpectation? Isolation { get; }

    private ScenarioKind Kind { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}
