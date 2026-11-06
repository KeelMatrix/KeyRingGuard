using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard.Tests;

// The integration suite and isolated package consumer execute the same real-crypto boundary cases.
internal static class BufferBoundaryProbe
{
    internal static async Task RunAsync(string store)
    {
        var count = 0;
        foreach (var asynchronous in new[] { false, true })
        {
            foreach (var sameApplication in new[] { false, true })
                foreach (var acceptedDirection in new[] { 0, 1, 2 })
                {
                    var boundary = new KeyRingBoundary();
                    KeyRingProviderFactory Factory(string application, bool rejectCross = false) => Create(asynchronous,
                        () => new ConsumingProvider(DataProtectionProvider.Create(new DirectoryInfo(store), b => b.SetApplicationName(application)), rejectCross), boundary);
                    var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(KeyRingScenario.ApplicationIsolation,
                        Factory("Buffer.App", sameApplication && acceptedDirection == 1),
                        Factory(sameApplication ? "Buffer.App" : "Other.App", sameApplication && acceptedDirection == 2),
                        new KeyRingIsolationControl(Factory("Buffer.App"), Factory("Buffer.App")), CancellationToken.None);
                    Require(result.FailureKind == (sameApplication ? KeyRingFailureKind.UnexpectedCrossUnprotect : KeyRingFailureKind.None), "Consuming isolation cross direction");
                    count++;
                }

            foreach (var scenario in new[] { KeyRingScenario.RestartContinuity, KeyRingScenario.ReplicaSharing, KeyRingScenario.PurposeIsolation })
            {
                // Separate wrappers also reuse the same output buffer across callbacks.
                var output = new ReusedOutput();
                var factory = Create(asynchronous, () => new ConsumingProvider(
                    DataProtectionProvider.Create(new DirectoryInfo(store), b => b.SetApplicationName("Buffer.App")), output: output));
                var result = await KeyRingVerifier.VerifyAsync(scenario, factory, factory);
                Require(result.Succeeded, $"Consuming {scenario}: {result.FailureKind}");
                count++;
            }
        }
        Console.WriteLine($"BUFFER_BOUNDARIES: PASS cases={count}");
    }

    private static KeyRingProviderFactory Create(bool asynchronous, Func<IDataProtectionProvider> create, KeyRingBoundary? boundary = null) => asynchronous
        ? new(async _ => { await Task.Yield(); return create(); }, TimeSpan.FromSeconds(5), null, boundary ?? new KeyRingBoundary())
        : new(_ => create(), TimeSpan.FromSeconds(5), null, boundary ?? new KeyRingBoundary());

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class ReusedOutput
    {
        private byte[]? _buffer;
        internal byte[] Capture(byte[] value)
        {
            if (_buffer?.Length != value.Length) { _buffer = new byte[value.Length]; }
            value.CopyTo(_buffer, 0);
            return _buffer;
        }
        internal void Clear() { if (_buffer is not null) { Array.Clear(_buffer); } }
    }

    private sealed class ConsumingProvider(IDataProtectionProvider inner, bool rejectCross = false, ReusedOutput? output = null) : IDataProtectionProvider, IDisposable
    {
        private int _reads;
        public IDataProtector CreateProtector(string purpose) => new ConsumingProtector(inner.CreateProtector(purpose),
            () => rejectCross && Interlocked.Increment(ref _reads) > 1, output);
        public void Dispose() => output?.Clear();
    }

    private sealed class ConsumingProtector(IDataProtector inner, Func<bool> reject, ReusedOutput? output) : IPersistedDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => new ConsumingProtector(inner.CreateProtector(purpose), reject, output);
        public byte[] Protect(byte[] plaintext) => output?.Capture(inner.Protect(plaintext)) ?? inner.Protect(plaintext);
        public byte[] Unprotect(byte[] protectedData)
        {
            if (reject()) { throw new System.Security.Cryptography.CryptographicException(); }
            var recovered = inner.Unprotect(protectedData);
            Array.Clear(protectedData);
            output?.Clear();
            return recovered;
        }
        public byte[] DangerousUnprotect(byte[] protectedData, bool ignoreRevocationErrors, out bool requiresMigration, out bool wasRevoked)
        {
            requiresMigration = false;
            wasRevoked = false;
            return Unprotect(protectedData);
        }
    }
}
