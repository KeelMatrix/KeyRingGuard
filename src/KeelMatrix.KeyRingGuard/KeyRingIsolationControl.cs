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
    /// <remarks>
    /// All four factories used for an application-isolation verification must carry the same
    /// <see cref="KeyRingBoundary"/> instance. The identity is caller-declared because arbitrary
    /// provider factories do not expose their backing-store configuration.
    /// </remarks>
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

    internal bool IsLinkedTo(KeyRingProviderFactory primaryFactory, KeyRingProviderFactory secondaryFactory) =>
        PrimaryFactory.Boundary is not null
        && ReferenceEquals(PrimaryFactory.Boundary, SecondaryFactory.Boundary)
        && ReferenceEquals(PrimaryFactory.Boundary, primaryFactory.Boundary)
        && ReferenceEquals(PrimaryFactory.Boundary, secondaryFactory.Boundary);
}
