using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Defines how KeyRingGuard constructs an independent Data Protection provider.
/// Each synchronous or asynchronous operation is observed with the configured bound by the verifier.
/// A provider remains alive until its scheduled callbacks finish, including callbacks that outlive a timeout.
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
    /// The verifier takes the opaque key-ID snapshot and invokes the rotation callback in one
    /// bounded scheduled operation, with no verifier work or second scheduler admission between them.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, key observation, or rotation operation.</param>
    /// <param name="createKeyManager">Creates a key manager for rotation verification.</param>
    /// <param name="createNewKey">Creates and returns the newly persisted key, honoring cancellation before and during the mutation.</param>
    /// <remarks>
    /// The callback must not return a pre-existing key, even if that key is active.
    /// </remarks>
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
    /// The verifier takes the opaque key-ID snapshot and invokes the rotation callback in one
    /// bounded scheduled operation, with no verifier work or second scheduler admission between them.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, key observation, or rotation operation.</param>
    /// <param name="createKeyManager">Creates a key manager for rotation verification.</param>
    /// <param name="createNewKey">Creates and returns the newly persisted key, honoring cancellation before and during the mutation.</param>
    /// <param name="boundary">The identity shared by factories configured for the same backing store.</param>
    /// <remarks>
    /// The callback must not return a pre-existing key, even if that key is active.
    /// </remarks>
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
    /// The verifier takes the opaque key-ID snapshot and invokes the rotation callback in one
    /// bounded scheduled operation, with no verifier work or second scheduler admission between them.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, key observation, or rotation operation.</param>
    /// <param name="createKeyManager">Creates a key manager for rotation verification.</param>
    /// <param name="createNewKey">Creates and returns the newly persisted key, honoring cancellation before and during the mutation.</param>
    /// <remarks>
    /// The callback must not return a pre-existing key, even if that key is active.
    /// </remarks>
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
    /// The verifier takes the opaque key-ID snapshot and invokes the rotation callback in one
    /// bounded scheduled operation, with no verifier work or second scheduler admission between them.
    /// </summary>
    /// <param name="createProvider">Creates a provider using the caller's configuration.</param>
    /// <param name="timeout">The maximum time allowed for each provider, protector, protection, unprotection, key observation, or rotation operation.</param>
    /// <param name="createKeyManager">Creates a key manager for rotation verification.</param>
    /// <param name="createNewKey">Creates and returns the newly persisted key, honoring cancellation before and during the mutation.</param>
    /// <param name="boundary">The identity shared by factories configured for the same backing store.</param>
    /// <remarks>
    /// The callback must not return a pre-existing key, even if that key is active.
    /// </remarks>
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

    internal Task<(HashSet<Guid> ExistingKeyIds, IKey NewKey)> CreateNewKeyWithSnapshotAsync(
        IKeyManager manager,
        DateTimeOffset activationDate,
        DateTimeOffset expirationDate,
        Func<IKeyManager, HashSet<Guid>> snapshotKeyIds,
        CancellationToken cancellationToken)
    {
        if (_createNewKey is null)
        {
            return Task.FromException<(HashSet<Guid> ExistingKeyIds, IKey NewKey)>(new InvalidOperationException());
        }

        return CreateNewKeyWithSnapshotCoreAsync(manager, activationDate, expirationDate, snapshotKeyIds, cancellationToken);
    }

    private async Task<(HashSet<Guid> ExistingKeyIds, IKey NewKey)> CreateNewKeyWithSnapshotCoreAsync(
        IKeyManager manager,
        DateTimeOffset activationDate,
        DateTimeOffset expirationDate,
        Func<IKeyManager, HashSet<Guid>> snapshotKeyIds,
        CancellationToken cancellationToken)
    {
        var existingKeyIds = snapshotKeyIds(manager);
        var newKey = await _createNewKey!(manager, activationDate, expirationDate, cancellationToken).ConfigureAwait(false);
        return (existingKeyIds, newKey);
    }

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
    private static readonly AsyncLocal<int> CallbackDepth = new();

    internal static bool IsInsideCallback => CallbackDepth.Value > 0;

    internal static Task<T> Run<T>(Func<T> operation, CancellationToken startCancellationToken) =>
        RunCoreAsync(operation, startCancellationToken);

    internal static Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken startCancellationToken) =>
        RunCoreAsync(operation, startCancellationToken);

    private static async Task<T> RunCoreAsync<T>(Func<T> operation, CancellationToken startCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await Slots.WaitAsync(startCancellationToken).ConfigureAwait(false);
        try
        {
            startCancellationToken.ThrowIfCancellationRequested();
            return await Task.Factory.StartNew(
                () => InvokeCallback(operation),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            Slots.Release();
        }
    }

    private static async Task<T> RunCoreAsync<T>(Func<Task<T>> operation, CancellationToken startCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await Slots.WaitAsync(startCancellationToken).ConfigureAwait(false);
        try
        {
            startCancellationToken.ThrowIfCancellationRequested();
            return await Task.Factory.StartNew(
                    () => InvokeCallbackAsync(operation),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap()
                .ConfigureAwait(false);
        }
        finally
        {
            Slots.Release();
        }
    }

    private static T InvokeCallback<T>(Func<T> operation)
    {
        var previousDepth = CallbackDepth.Value;
        CallbackDepth.Value = previousDepth + 1;
        try
        {
            return operation();
        }
        finally
        {
            CallbackDepth.Value = previousDepth;
        }
    }

    private static async Task<T> InvokeCallbackAsync<T>(Func<Task<T>> operation)
    {
        var previousDepth = CallbackDepth.Value;
        CallbackDepth.Value = previousDepth + 1;
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            CallbackDepth.Value = previousDepth;
        }
    }
}
