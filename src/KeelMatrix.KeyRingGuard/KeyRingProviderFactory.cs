using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Defines how KeyRingGuard constructs an independent Data Protection provider.
/// Each synchronous or asynchronous operation is observed with the configured bound by the verifier.
/// </summary>
public sealed class KeyRingProviderFactory
{
    private readonly Func<CancellationToken, IDataProtectionProvider>? _createProvider;
    private readonly Func<CancellationToken, Task<IDataProtectionProvider>>? _createProviderAsync;
    private readonly Func<CancellationToken, IKeyManager>? _createKeyManager;
    private readonly Func<CancellationToken, Task<IKeyManager>>? _createKeyManagerAsync;

    /// <summary>
    /// Initializes a synchronous provider factory.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, or key-manager operation.</param>
    public KeyRingProviderFactory(
        Func<CancellationToken, IDataProtectionProvider> createProvider,
        TimeSpan timeout)
        : this(createProvider, timeout, null, null, null, true)
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
        : this(createProvider, timeout, createKeyManager, null, null, true)
    {
    }

    /// <summary>
    /// Initializes a synchronous provider and cancellation-aware rotation operation.
    /// </summary>
    public KeyRingProviderFactory(
        Func<CancellationToken, IDataProtectionProvider> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, IKeyManager> createKeyManager,
        Func<IKeyManager, DateTimeOffset, DateTimeOffset, CancellationToken, Task<IKey>> createNewKey)
        : this(createProvider, timeout, createKeyManager, null, createNewKey, true)
    {
    }

    /// <summary>
    /// Initializes a synchronous provider and optional key-manager factory with a backing-store boundary identity.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, or key-manager operation.</param>
    /// <param name="createKeyManager">Creates a key manager for rotation verification.</param>
    /// <param name="boundary">The identity shared by factories configured for the same backing store.</param>
    public KeyRingProviderFactory(
        Func<CancellationToken, IDataProtectionProvider> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, IKeyManager>? createKeyManager,
        KeyRingBoundary boundary)
        : this(createProvider, timeout, createKeyManager, boundary, null, true)
    {
    }

    /// <summary>
    /// Initializes a synchronous provider, cancellation-aware rotation operation, and backing-store boundary identity.
    /// </summary>
    public KeyRingProviderFactory(
        Func<CancellationToken, IDataProtectionProvider> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, IKeyManager> createKeyManager,
        Func<IKeyManager, DateTimeOffset, DateTimeOffset, CancellationToken, Task<IKey>> createNewKey,
        KeyRingBoundary boundary)
        : this(createProvider, timeout, createKeyManager, boundary, createNewKey, true)
    {
    }

    private KeyRingProviderFactory(
        Func<CancellationToken, IDataProtectionProvider> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, IKeyManager>? createKeyManager,
        KeyRingBoundary? boundary,
        Func<IKeyManager, DateTimeOffset, DateTimeOffset, CancellationToken, Task<IKey>>? createNewKey,
        bool _)
    {
        ArgumentNullException.ThrowIfNull(createProvider);

        _createProvider = createProvider;
        _createKeyManager = createKeyManager;
        _createNewKey = createNewKey;
        Boundary = boundary;
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
        : this(createProvider, timeout, null, null, null, true)
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
        : this(createProvider, timeout, createKeyManager, null, null, true)
    {
    }

    /// <summary>
    /// Initializes an asynchronous provider and cancellation-aware rotation operation.
    /// </summary>
    public KeyRingProviderFactory(
        Func<CancellationToken, Task<IDataProtectionProvider>> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, Task<IKeyManager>> createKeyManager,
        Func<IKeyManager, DateTimeOffset, DateTimeOffset, CancellationToken, Task<IKey>> createNewKey)
        : this(createProvider, timeout, createKeyManager, null, createNewKey, true)
    {
    }

    /// <summary>
    /// Initializes an asynchronous provider and optional key-manager factory with a backing-store boundary identity.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, or key-manager operation.</param>
    /// <param name="createKeyManager">Creates a key manager for rotation verification.</param>
    /// <param name="boundary">The identity shared by factories configured for the same backing store.</param>
    public KeyRingProviderFactory(
        Func<CancellationToken, Task<IDataProtectionProvider>> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, Task<IKeyManager>>? createKeyManager,
        KeyRingBoundary boundary)
        : this(createProvider, timeout, createKeyManager, boundary, null, true)
    {
    }

    /// <summary>
    /// Initializes an asynchronous provider, cancellation-aware rotation operation, and backing-store boundary identity.
    /// </summary>
    public KeyRingProviderFactory(
        Func<CancellationToken, Task<IDataProtectionProvider>> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, Task<IKeyManager>> createKeyManager,
        Func<IKeyManager, DateTimeOffset, DateTimeOffset, CancellationToken, Task<IKey>> createNewKey,
        KeyRingBoundary boundary)
        : this(createProvider, timeout, createKeyManager, boundary, createNewKey, true)
    {
    }

    private KeyRingProviderFactory(
        Func<CancellationToken, Task<IDataProtectionProvider>> createProvider,
        TimeSpan timeout,
        Func<CancellationToken, Task<IKeyManager>>? createKeyManager,
        KeyRingBoundary? boundary,
        Func<IKeyManager, DateTimeOffset, DateTimeOffset, CancellationToken, Task<IKey>>? createNewKey,
        bool _)
    {
        ArgumentNullException.ThrowIfNull(createProvider);

        _createProviderAsync = createProvider;
        _createKeyManagerAsync = createKeyManager;
        _createNewKey = createNewKey;
        Boundary = boundary;
        Timeout = ValidateTimeout(timeout);
    }

    private readonly Func<IKeyManager, DateTimeOffset, DateTimeOffset, CancellationToken, Task<IKey>>? _createNewKey;

    /// <summary>
    /// Gets the per-operation timeout applied by the verifier.
    /// </summary>
    public TimeSpan Timeout { get; }

    internal KeyRingBoundary? Boundary { get; }

    internal bool HasKeyManagerFactory => _createKeyManager is not null || _createKeyManagerAsync is not null;

    internal Task<IDataProtectionProvider> CreateProviderAsync(CancellationToken cancellationToken) =>
        _createProviderAsync is not null
            ? KeyRingOperationScheduler.RunAsync(() => _createProviderAsync(cancellationToken), cancellationToken)
            : KeyRingOperationScheduler.Run(() => _createProvider!(cancellationToken), cancellationToken);

    internal Task<IKeyManager> CreateKeyManagerAsync(CancellationToken cancellationToken) =>
        _createKeyManagerAsync is not null
            ? KeyRingOperationScheduler.RunAsync(() => _createKeyManagerAsync(cancellationToken), cancellationToken)
            : _createKeyManager is null
                ? Task.FromException<IKeyManager>(new InvalidOperationException())
                : KeyRingOperationScheduler.Run(() => _createKeyManager(cancellationToken), cancellationToken);

    internal bool HasNewKeyFactory => _createNewKey is not null;

    internal Task<IKey> CreateNewKeyAsync(
        IKeyManager manager,
        DateTimeOffset activationDate,
        DateTimeOffset expirationDate,
        CancellationToken cancellationToken) =>
        _createNewKey is null
            ? Task.FromException<IKey>(new InvalidOperationException())
            : KeyRingOperationScheduler.RunAsync(
                () => _createNewKey(manager, activationDate, expirationDate, cancellationToken),
                cancellationToken);

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be greater than zero and no more than one day.");
        }

        return timeout;
    }
}

internal static class KeyRingOperationScheduler
{
    private static readonly SemaphoreSlim Slots = new(32, 32);

    internal static Task<T> Run<T>(Func<T> operation, CancellationToken startCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return Task.Run(async () =>
        {
            await Slots.WaitAsync().ConfigureAwait(false);
            try
            {
                startCancellationToken.ThrowIfCancellationRequested();
                return operation();
            }
            finally
            {
                Slots.Release();
            }
        });
    }

    internal static Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken startCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return Task.Run(async () =>
        {
            await Slots.WaitAsync().ConfigureAwait(false);
            try
            {
                startCancellationToken.ThrowIfCancellationRequested();
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                Slots.Release();
            }
        });
    }
}
