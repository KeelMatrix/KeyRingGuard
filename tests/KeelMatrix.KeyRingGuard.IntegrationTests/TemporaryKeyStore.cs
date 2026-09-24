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
    {
        return new KeyRingProviderFactory(
            _ =>
            {
                var provider = DataProtectionProvider.Create(
                    new DirectoryInfo(Path),
                    builder => builder.SetApplicationName(applicationName));
                return provider;
            },
            TimeSpan.FromSeconds(5));
    }

    public KeyRingProviderFactory CreateRotationFactory(string applicationName)
    {
        var services = new ServiceCollection();
        services
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path))
            .SetApplicationName(applicationName);
        var serviceProvider = services.BuildServiceProvider();
        _ownedResources.Add(serviceProvider);

        return new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ =>
            {
                var provider = DataProtectionProvider.Create(
                    new DirectoryInfo(Path),
                    builder => builder.SetApplicationName(applicationName));
                return provider;
            }),
            TimeSpan.FromSeconds(5),
            (Func<CancellationToken, IKeyManager>)(_ => serviceProvider.GetRequiredService<IKeyManager>()));
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
