using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace KeelMatrix.KeyRingGuard.IntegrationTests;

internal sealed class TemporaryKeyStore : IDisposable
{
    private readonly List<IDisposable> _ownedResources = [];

    public TemporaryKeyStore()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keyringguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public KeyRingProviderFactory CreateFactory(string applicationName)
        => CreateFactory(applicationName, null);

    public KeyRingProviderFactory CreateFactory(string applicationName, KeyRingBoundary? boundary)
    {
        return boundary is null
            ? new KeyRingProviderFactory(
                _ =>
                {
                    var provider = DataProtectionProvider.Create(
                        new DirectoryInfo(Path),
                        builder => builder.SetApplicationName(applicationName));
                    return provider;
                },
                TimeSpan.FromSeconds(5))
            : new KeyRingProviderFactory(
                _ =>
                {
                    var provider = DataProtectionProvider.Create(
                        new DirectoryInfo(Path),
                        builder => builder.SetApplicationName(applicationName));
                    return provider;
                },
                TimeSpan.FromSeconds(5),
                null,
                boundary);
    }

    public KeyRingProviderFactory CreateAsyncFactory(string applicationName)
        => new(
            async _ =>
            {
                await Task.Yield();
                return DataProtectionProvider.Create(
                    new DirectoryInfo(Path),
                    builder => builder.SetApplicationName(applicationName));
            },
            TimeSpan.FromSeconds(5));

    public KeyRingProviderFactory CreateRotationFactory(string applicationName)
        => CreateRotationFactory(applicationName, (Func<CancellationToken, IKeyManager>?)null);

    public KeyRingProviderFactory CreateRotationFactory(
        string applicationName,
        Func<CancellationToken, IKeyManager>? managerFactory)
    {
        CreateInitialRotationKey(applicationName);
        var pendingManagers = new Queue<IKeyManager>();

        return new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ =>
            {
                var services = new ServiceCollection();
                services
                    .AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(Path))
                    .SetApplicationName(applicationName);
                services.Configure<KeyManagementOptions>(options => options.AutoGenerateKeys = false);
                var serviceProvider = services.BuildServiceProvider();
                _ownedResources.Add(serviceProvider);
                pendingManagers.Enqueue(serviceProvider.GetRequiredService<IKeyManager>());
                return new OwnedProvider(serviceProvider.GetRequiredService<IDataProtectionProvider>(), serviceProvider);
            }),
            TimeSpan.FromSeconds(5),
            managerFactory ?? (Func<CancellationToken, IKeyManager>)(_ => pendingManagers.Dequeue()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return manager.CreateNewKey(activationDate, expirationDate);
                }, cancellationToken));
    }

    public KeyRingProviderFactory CreateAsyncRotationFactory(string applicationName)
    {
        CreateInitialRotationKey(applicationName);
        var pendingManagers = new Queue<IKeyManager>();

        return new KeyRingProviderFactory(
            async _ =>
            {
                await Task.Yield();
                var services = new ServiceCollection();
                services
                    .AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(Path))
                    .SetApplicationName(applicationName);
                services.Configure<KeyManagementOptions>(options => options.AutoGenerateKeys = false);
                var serviceProvider = services.BuildServiceProvider();
                _ownedResources.Add(serviceProvider);
                pendingManagers.Enqueue(serviceProvider.GetRequiredService<IKeyManager>());
                return new OwnedProvider(serviceProvider.GetRequiredService<IDataProtectionProvider>(), serviceProvider);
            },
            TimeSpan.FromSeconds(5),
            async _ =>
            {
                await Task.Yield();
                return pendingManagers.Dequeue();
            },
            async (manager, activationDate, expirationDate, cancellationToken) =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return manager.CreateNewKey(activationDate, expirationDate);
            });
    }

    public KeyRingProviderFactory CreateRotationFactory(string applicationName, KeyRingBoundary boundary)
    {
        CreateInitialRotationKey(applicationName);
        var pendingManagers = new Queue<IKeyManager>();

        return new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ =>
            {
                var services = new ServiceCollection();
                services
                    .AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(Path))
                    .SetApplicationName(applicationName);
                services.Configure<KeyManagementOptions>(options => options.AutoGenerateKeys = false);
                var serviceProvider = services.BuildServiceProvider();
                _ownedResources.Add(serviceProvider);
                pendingManagers.Enqueue(serviceProvider.GetRequiredService<IKeyManager>());
                return new OwnedProvider(serviceProvider.GetRequiredService<IDataProtectionProvider>(), serviceProvider);
            }),
            TimeSpan.FromSeconds(5),
            (Func<CancellationToken, IKeyManager>)(_ => pendingManagers.Dequeue()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return manager.CreateNewKey(activationDate, expirationDate);
                }, cancellationToken),
            boundary);
    }

    private void CreateInitialRotationKey(string applicationName)
    {
        var services = new ServiceCollection();
        services
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path))
            .SetApplicationName(applicationName);
        services.Configure<KeyManagementOptions>(options => options.AutoGenerateKeys = false);
        var serviceProvider = services.BuildServiceProvider();
        _ownedResources.Add(serviceProvider);
        serviceProvider.GetRequiredService<IKeyManager>().CreateNewKey(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    public Func<CancellationToken, IKeyManager> CreateKeyManagerFactory(string applicationName)
    {
        var services = new ServiceCollection();
        services
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path))
            .SetApplicationName(applicationName);
        var serviceProvider = services.BuildServiceProvider();
        _ownedResources.Add(serviceProvider);
        return _ => serviceProvider.GetRequiredService<IKeyManager>();
    }

    public void Dispose()
    {
        for (var index = _ownedResources.Count - 1; index >= 0; index--)
        {
            _ownedResources[index].Dispose();
        }

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class OwnedProvider : IDataProtectionProvider, IDisposable
{
    private readonly IDataProtectionProvider _provider;
    private readonly IServiceProvider _serviceProvider;

    public OwnedProvider(IDataProtectionProvider provider, IServiceProvider serviceProvider)
    {
        _provider = provider;
        _serviceProvider = serviceProvider;
    }

    public IDataProtector CreateProtector(string purpose) => _provider.CreateProtector(purpose);

    public void Dispose() => (_serviceProvider as IDisposable)?.Dispose();
}
