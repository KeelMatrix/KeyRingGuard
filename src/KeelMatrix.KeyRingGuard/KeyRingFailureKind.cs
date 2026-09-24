namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Identifies the bounded failure category returned by a verification.
/// </summary>
public enum KeyRingFailureKind
{
    /// <summary>
    /// The verification completed successfully.
    /// </summary>
    None = 0,

    /// <summary>
    /// The scenario arguments do not contain the required provider factories.
    /// </summary>
    InvalidScenario,

    /// <summary>
    /// A provider could not be constructed.
    /// </summary>
    ProviderCreation,

    /// <summary>
    /// Protecting the synthetic canary failed.
    /// </summary>
    Protect,

    /// <summary>
    /// Unprotecting a payload that should be readable failed.
    /// </summary>
    Unprotect,

    /// <summary>
    /// A provider unexpectedly unprotected a payload from an isolated boundary.
    /// </summary>
    UnexpectedCrossUnprotect,

    /// <summary>
    /// The configured provider or key-manager operation exceeded its bound.
    /// </summary>
    Timeout,

    /// <summary>
    /// The caller canceled the verification.
    /// </summary>
    Canceled,

    /// <summary>
    /// Rotation was requested without a caller-supplied key-manager factory.
    /// </summary>
    RotationUnavailable,

    /// <summary>
    /// The caller-supplied key-manager operation failed.
    /// </summary>
    RotationFailure,

    /// <summary>
    /// An optional non-secret key-ring metadata check failed.
    /// </summary>
    MetadataHealth
}
