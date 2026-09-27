namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Describes the same-boundary control used to attribute application-isolation rejection.
/// </summary>
public sealed class KeyRingIsolationControl
{
    /// <summary>
    /// Initializes a control whose factories must use the same key store and application discriminator.
    /// </summary>
    /// <param name="primaryFactory">Creates the first control provider.</param>
    /// <param name="secondaryFactory">Creates the second control provider.</param>
    public KeyRingIsolationControl(
        KeyRingProviderFactory primaryFactory,
        KeyRingProviderFactory secondaryFactory)
    {
        ArgumentNullException.ThrowIfNull(primaryFactory);
        ArgumentNullException.ThrowIfNull(secondaryFactory);

        PrimaryFactory = primaryFactory;
        SecondaryFactory = secondaryFactory;
    }

    /// <summary>
    /// Gets the first same-boundary control factory.
    /// </summary>
    public KeyRingProviderFactory PrimaryFactory { get; }

    /// <summary>
    /// Gets the second same-boundary control factory.
    /// </summary>
    public KeyRingProviderFactory SecondaryFactory { get; }
}
