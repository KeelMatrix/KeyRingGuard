using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

public sealed partial class VerifierFailureTests
{
    [Fact]
    public async Task RotationRequiresExplicitKeyManagerFactory()
    {
        var factory = new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationUnavailable, result.FailureKind);
    }

    [Fact]
    public async Task RotationRequiresAnObservedNewActiveKey()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromSeconds(1),
            (Func<CancellationToken, IKeyManager>)(_ => new NullKeyManager()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(manager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationRejectsCallbackReturningPreExistingKey()
    {
        var manager = new ExistingKeyManager();
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task AsyncRotationRejectsCallbackReturningPreExistingKey()
    {
        var manager = new ExistingKeyManager();
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
                Task.FromResult<IDataProtectionProvider>(new FakeProvider())),
            TimeSpan.FromSeconds(1),
            (Func<CancellationToken, Task<IKeyManager>>)(_ => Task.FromResult<IKeyManager>(manager)),
            async (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return keyManager.CreateNewKey(activationDate, expirationDate);
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationSnapshotsAndInvokesCallbackWithinOneScheduledOperation()
    {
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId)
                    : payload.ToArray(),
                unprotect: UnprotectPayload)),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, _) =>
            {
                Assert.True(RotationThreadProbe.SnapshotSeenOnCurrentThread);
                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task AsyncRotationCallbackHonorsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var callbackStarted = new ManualResetEventSlim();
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
                Task.FromResult<IDataProtectionProvider>(new FakeProvider())),
            TimeSpan.FromSeconds(5),
            (Func<CancellationToken, Task<IKeyManager>>)(_ => Task.FromResult<IKeyManager>(new NullKeyManager())),
            async (manager, activationDate, expirationDate, cancellationToken) =>
            {
                callbackStarted.Set();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return manager.CreateNewKey(activationDate, expirationDate);
            });

        var verification = KeyRingVerifier.VerifyAsync(
            KeyRingScenario.RotationContinuity,
            factory,
            cancellationToken: cancellation.Token);
        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(1)));

        cancellation.Cancel();
        var result = await verification;

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Canceled, result.FailureKind);
    }

    private static KeyRingProviderFactory CreateFastRotationFactory(bool failRotation = false)
    {
        var manager = new ThreadScopedRotationKeyManager();
        return new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId)
                    : payload.ToArray(),
                unprotect: UnprotectPayload)),
            TimeSpan.FromSeconds(2),
            (Func<CancellationToken, IKeyManager>)(_ => manager),
            (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failRotation)
                {
                    throw new InvalidOperationException("rotation callback failure");
                }

                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("expired")]
    [InlineData("not-yet-active")]
    [InlineData("revoked")]
    [InlineData("mismatched")]
    public async Task RotationRejectsUnsafeObservedKeyMetadata(string caseName)
    {
        var manager = new MetadataKeyManager(caseName);
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationObservationExceptionsAreSanitizedAndBounded()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromSeconds(1),
            (Func<CancellationToken, IKeyManager>)(_ => new ThrowingObservationKeyManager()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(manager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationTreatsDistinctAlternateBytesAsFailedCanonicalAdoption()
    {
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId, bigEndian: true)
                    : payload.ToArray(),
                unprotect: UnprotectPayload),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationReportsStandardPayloadWithoutAdoptionAsRotationFailure()
    {
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } ? CreateProtectedPayload(payload, Guid.NewGuid()) : payload.ToArray(),
                unprotect: UnprotectPayload),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
        Assert.Contains("did not adopt", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("zero")]
    [InlineData("short")]
    [InlineData("truncated-key-id")]
    [InlineData("malformed")]
    [InlineData("empty-standard")]
    public async Task RotationReportsUnsupportedPayloadFormatAsUnavailable(string payloadKind)
    {
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? payloadKind switch
                    {
                        "opaque" => CreateOpaqueProtectedPayload(payload),
                        "zero" => [],
                        "short" => [0x09, 0xF0, 0xC9, 0xF0],
                        "truncated-key-id" => [0x09, 0xF0, 0xC9, 0xF0, .. new byte[15]],
                        "malformed" => [0x08, 0xF0, 0xC9, 0xF0, .. new byte[16], .. payload],
                        "empty-standard" => CreateProtectedPayload([], key.KeyId),
                        _ => throw new ArgumentOutOfRangeException(nameof(payloadKind)),
                    }
                    : payload.ToArray(),
                unprotect: UnprotectOpaquePayload),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationUnavailable, result.FailureKind);
        Assert.Contains("standard ASP.NET Core Data Protection payload format", result.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateProtectedPayload(byte[] payload, Guid keyId, bool bigEndian = false)
    {
        var protectedPayload = new byte[20 + payload.Length];
        protectedPayload[0] = 0x09;
        protectedPayload[1] = 0xF0;
        protectedPayload[2] = 0xC9;
        protectedPayload[3] = 0xF0;
        if (bigEndian)
        {
            keyId.TryWriteBytes(protectedPayload.AsSpan(4, 16), bigEndian: true, out _);
        }
        else
        {
            keyId.TryWriteBytes(protectedPayload.AsSpan(4, 16));
        }

        payload.CopyTo(protectedPayload.AsSpan(20));
        return protectedPayload;
    }

    private static byte[] CreateOpaqueProtectedPayload(byte[] payload)
    {
        var protectedPayload = new byte[32 + payload.Length];
        RandomNumberGenerator.Fill(protectedPayload.AsSpan(0, 32));
        payload.CopyTo(protectedPayload.AsSpan(32));
        return protectedPayload;
    }

    private static byte[] UnprotectPayload(byte[] protectedPayload) =>
        protectedPayload.Length >= 20
            && protectedPayload[0] == 0x09
            && protectedPayload[1] == 0xF0
            && protectedPayload[2] == 0xC9
            && protectedPayload[3] == 0xF0
            ? protectedPayload[20..]
            : protectedPayload.ToArray();

    private static byte[] UnprotectOpaquePayload(byte[] protectedPayload) =>
        protectedPayload.Length >= 32
            ? protectedPayload[32..]
            : protectedPayload.ToArray();
}
