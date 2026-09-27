namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Identifies the caller-declared backing-store boundary shared by provider factories.
/// </summary>
/// <remarks>
/// KeyRingGuard cannot inspect an arbitrary provider factory to prove that two factories use the same
/// store. Use one boundary instance for every factory that is intended to represent the same store,
/// and use different instances for different stores. This identity is used only to prevent an
/// unrelated isolation control from being accepted as evidence.
/// </remarks>
public sealed class KeyRingBoundary
{
}
