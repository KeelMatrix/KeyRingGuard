using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace KeelMatrix.KeyRingGuard.Tests;

internal static class RestartBoundaryProbe
{
    internal static async Task RunAsync(string root)
    {
        var count = 0;
        foreach (var asynchronous in new[] { false, true })
            foreach (var mode in new[] { "persistent", "exclusive", "volatile", "non-disposable" })
            {
                var store = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));
                var calls = 0;
                var live = 0;
                var disposed = 0;
                IDataProtectionProvider Create()
                {
                    if (mode == "exclusive" && Volatile.Read(ref live) != 0) { throw new InvalidOperationException("The previous owner is live."); }
                    Interlocked.Increment(ref calls);
                    if (mode == "non-disposable") { return DataProtectionProvider.Create(store, b => b.SetApplicationName("Restart.App")); }
                    var services = new ServiceCollection();
                    services.AddDataProtection().PersistKeysToFileSystem(store).SetApplicationName("Restart.App");
                    var owner = services.BuildServiceProvider();
                    Interlocked.Increment(ref live);
                    return new Owner(owner.GetRequiredService<IDataProtectionProvider>(), () =>
                    {
                        owner.Dispose();
                        Interlocked.Decrement(ref live);
                        Interlocked.Increment(ref disposed);
                        if (mode == "volatile") { store = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))); }
                    });
                }
                var factory = asynchronous
                    ? new KeyRingProviderFactory(async _ => { await Task.Yield(); return Create(); }, TimeSpan.FromSeconds(5))
                    : new KeyRingProviderFactory(_ => Create(), TimeSpan.FromSeconds(5));
                var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);
                Require(result.FailureKind == (mode == "volatile" ? KeyRingFailureKind.Unprotect : KeyRingFailureKind.None), $"Restart mode={mode} result={result.FailureKind}");
                Require(calls == 2 && live == 0 && disposed == (mode == "non-disposable" ? 0 : 2), "Restart owner sequence");
                count++;
            }
        Console.WriteLine($"RESTART_BOUNDARIES: PASS cases={count}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class Owner(IDataProtectionProvider inner, Action dispose) : IDataProtectionProvider, IDisposable
    {
        public IDataProtector CreateProtector(string purpose) => inner.CreateProtector(purpose);
        public void Dispose() => dispose();
    }
}
