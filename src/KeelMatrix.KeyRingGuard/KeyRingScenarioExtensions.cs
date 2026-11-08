namespace KeelMatrix.KeyRingGuard;

internal static class KeyRingScenarioExtensions
{
    internal static bool IsRestartContinuity(this KeyRingScenario scenario) => scenario == KeyRingScenario.RestartContinuity;

    internal static bool IsReplicaSharing(this KeyRingScenario scenario) => scenario == KeyRingScenario.ReplicaSharing;

    internal static bool IsApplicationIsolation(this KeyRingScenario scenario) => scenario == KeyRingScenario.ApplicationIsolation;

    internal static bool IsPurposeIsolation(this KeyRingScenario scenario) => scenario == KeyRingScenario.PurposeIsolation;

    internal static bool IsRotationContinuity(this KeyRingScenario scenario) => scenario == KeyRingScenario.RotationContinuity;
}
