using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Defines how KeyRingGuard constructs an independent Data Protection provider.
/// Each synchronous or asynchronous operation is observed with the configured bound by the verifier.
/// </summary>
public sealed class KeyRingProviderFactory
{
    private readonly Func<CancellationToken, Task<IDataProtectionProvider>> _createProvider;
    private readonly Func<CancellationToken, Task<IKeyManager>>? _createKeyManager;

    /// <summary>
    /// Initializes a synchronous provider factory.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, or key-manager operation.</param>
    public KeyRingProviderFactory(
        Func<CancellationToken, IDataProtectionProvider> createProvider,
        TimeSpan timeout)
        : this(createProvider, timeout, null)
    {
    }

    /// <summary>
    /// Initializes a synchronous provider and key-manager factory.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, or key-manager operation.</param>
    /// <param name="createKeyManager">Creates a key manager for rotation verification.</param>
    public KeyRingProviderFactory(
        Func<CancellationToken, IDataProtectionProvider> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, IKeyManager>? createKeyManager)
    {
        ArgumentNullException.ThrowIfNull(createProvider);

        _createProvider = cancellationToken => Task.Factory.StartNew(
            () => createProvider(cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _createKeyManager = createKeyManager is null
            ? null
            : cancellationToken => Task.Factory.StartNew(
                () => createKeyManager(cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        Timeout = ValidateTimeout(timeout);
    }

    /// <summary>
    /// Initializes an asynchronous provider factory.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, or key-manager operation.</param>
    public KeyRingProviderFactory(
        Func<CancellationToken, Task<IDataProtectionProvider>> createProvider,
        TimeSpan timeout)
        : this(createProvider, timeout, null)
    {
    }

    /// <summary>
    /// Initializes an asynchronous provider and key-manager factory.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, or key-manager operation.</param>
    /// <param name="createKeyManager">Creates a key manager for rotation verification.</param>
    public KeyRingProviderFactory(
        Func<CancellationToken, Task<IDataProtectionProvider>> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, Task<IKeyManager>>? createKeyManager)
    {
        ArgumentNullException.ThrowIfNull(createProvider);

        _createProvider = cancellationToken => Task.Run(
            () => createProvider(cancellationToken));
        _createKeyManager = createKeyManager is null
            ? null
            : cancellationToken => Task.Run(() => createKeyManager(cancellationToken));
        Timeout = ValidateTimeout(timeout);
    }

    /// <summary>
    /// Gets the per-operation timeout applied by the verifier.
    /// </summary>
    public TimeSpan Timeout { get; }

    internal bool HasKeyManagerFactory => _createKeyManager is not null;

    internal Task<IDataProtectionProvider> CreateProviderAsync(CancellationToken cancellationToken) =>
        _createProvider(cancellationToken);

    internal Task<IKeyManager> CreateKeyManagerAsync(CancellationToken cancellationToken) =>
        _createKeyManager is null
            ? Task.FromException<IKeyManager>(new InvalidOperationException())
            : _createKeyManager(cancellationToken);

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be greater than zero and no more than one day.");
        }

        return timeout;
    }
}
