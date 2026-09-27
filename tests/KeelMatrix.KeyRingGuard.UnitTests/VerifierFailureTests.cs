using System.Security.Cryptography;
using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

public sealed class VerifierFailureTests
{
    [Fact]
    public async Task ProviderFailureIsCategorizedWithoutExceptionText()
    {
        const string secret = "<key secret> provider-path=do-not-emit";
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>)(_ => throw new InvalidOperationException(secret)),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TimeoutIsBoundedWhenFactoryIgnoresCancellation()
    {
        var completion = new TaskCompletionSource<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new KeyRingProviderFactory(
            _ => completion.Task,
            TimeSpan.FromMilliseconds(50));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task LateProviderCompletionIsDisposedAfterATimeout()
    {
        var completion = new TaskCompletionSource<IDataProtectionProvider>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        var provider = new FakeProvider(dispose: () => Interlocked.Increment(ref disposed));
        var factory = new KeyRingProviderFactory(
            _ => completion.Task,
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        completion.SetResult(provider);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task LateSynchronousProviderCompletionIsDisposedAfterATimeout()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        var provider = new FakeProvider(dispose: () => Interlocked.Increment(ref disposed));
        var factory = new KeyRingProviderFactory(
            _ =>
            {
                completion.Task.GetAwaiter().GetResult();
                return provider;
            },
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        completion.SetResult(true);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RestartFactoryMustCreateAnIndependentProvider()
    {
        var provider = new FakeProvider();
        var factory = new KeyRingProviderFactory(_ => provider, TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
    }

    [Fact]
    public async Task ReplicaFactoriesMustCreateIndependentProviders()
    {
        var provider = new FakeProvider();
        var firstFactory = new KeyRingProviderFactory(_ => provider, TimeSpan.FromSeconds(1));
        var secondFactory = new KeyRingProviderFactory(_ => provider, TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.ReplicaSharing,
            firstFactory,
            secondFactory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
    }

    [Fact]
    public async Task CancellationIsReportedSeparately()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new KeyRingProviderFactory(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new FakeProvider();
            },
            TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Canceled, result.FailureKind);
    }

    [Fact]
    public async Task UnexpectedSuccessfulCrossUnprotectFailsClosed()
    {
        var factory = new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, factory, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.UnexpectedCrossUnprotect, result.FailureKind);
    }

    [Fact]
    public async Task ApplicationIsolationProviderFailureDoesNotPass()
    {
        var boundary = new KeyRingBoundary();
        var brokenFactory = new KeyRingProviderFactory(
            _ => new FakeProvider(unprotect: _ => throw new InvalidOperationException("unrelated provider failure")),
            TimeSpan.FromSeconds(1),
            null,
            boundary);
        var controlFactory = new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(1), null, boundary);

        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            brokenFactory,
            brokenFactory,
            new KeyRingIsolationControl(controlFactory, controlFactory),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Fact]
    public async Task PurposeIsolationProviderFailureDoesNotPass()
    {
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(unprotect: _ => throw new InvalidOperationException("unrelated provider failure")),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, factory, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Fact]
    public async Task CryptographicExceptionInSameBoundaryControlDoesNotPassAsIsolation()
    {
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(unprotect: _ => throw new CryptographicException("rejection")),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, factory, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Fact]
    public async Task SynchronousProviderFactoryTimeoutIsBounded()
    {
        var factory = new KeyRingProviderFactory(
            _ =>
            {
                Thread.Sleep(250);
                return new FakeProvider();
            },
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task AsyncProviderFactoryInvocationTimeoutIsBoundedBeforeItReturnsATask()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
            {
                Thread.Sleep(250);
                return Task.FromResult<IDataProtectionProvider>(new FakeProvider());
            }),
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task SynchronousProtectTimeoutIsBounded()
    {
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(protect: value =>
            {
                Thread.Sleep(250);
                return value.ToArray();
            }),
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task SynchronousUnprotectTimeoutIsBounded()
    {
        var factoryCalls = 0;
        var factory = new KeyRingProviderFactory(
            _ =>
            {
                if (Interlocked.Increment(ref factoryCalls) == 2)
                {
                    return new FakeProvider(unprotect: value =>
                    {
                        Thread.Sleep(250);
                        return value.ToArray();
                    });
                }

                return new FakeProvider();
            },
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task SynchronousKeyManagerCreationTimeoutIsBounded()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromMilliseconds(40),
            (Func<CancellationToken, IKeyManager>)(_ =>
            {
                Thread.Sleep(250);
                return new SlowKeyManager();
            }),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(manager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task SynchronousRotationTimeoutIsBounded()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromMilliseconds(40),
            (Func<CancellationToken, IKeyManager>)(_ => new SlowKeyManager()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.Run(() => manager.CreateNewKey(activationDate, expirationDate), cancellationToken));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task RotationCallbackInvocationTimeoutIsBoundedBeforeItReturnsATask()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromMilliseconds(40),
            (Func<CancellationToken, IKeyManager>)(_ => new NullKeyManager()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
            {
                Thread.Sleep(250);
                return Task.FromResult<IKey>(new TestKey(Guid.NewGuid(), activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task MissingSecondaryFactoryIsAnInvalidScenario()
    {
        var factory = new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.InvalidScenario, result.FailureKind);
    }

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
    public async Task EveryCreatedProviderIsDisposedAcrossScenarioAndEarlyExitPaths()
    {
        var disposed = 0;
        KeyRingProviderFactory Factory() => new(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1));

        await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, Factory());
        await KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, Factory(), Factory());
        await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, Factory());

        var boundary = new KeyRingBoundary();
        await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1), null, boundary),
            new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1), null, boundary),
            new KeyRingIsolationControl(
                new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1), null, boundary),
                new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1), null, boundary)),
            CancellationToken.None);

        await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.RotationContinuity,
            new KeyRingProviderFactory(
                _ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)),
                TimeSpan.FromSeconds(1),
                _ => new NullKeyManager(),
                static (manager, activationDate, expirationDate, cancellationToken) =>
                    Task.FromResult(manager.CreateNewKey(activationDate, expirationDate))));

        Assert.Equal(11, disposed);
    }

    [Fact]
    public async Task DiagnosticsDoNotEchoPlaintextProtectedPayloadOrKeyXml()
    {
        byte[]? plaintext = null;
        byte[]? protectedPayload = null;
        const string keyXml = "<key id=\"sensitive\">master material</key>";
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: value =>
                {
                    plaintext = value.ToArray();
                    protectedPayload = [9, 8, 7, 6];
                    throw new InvalidOperationException($"{keyXml} {Convert.ToBase64String(value)}");
                }),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Protect, result.FailureKind);
        Assert.NotNull(plaintext);
        Assert.NotNull(protectedPayload);
        Assert.DoesNotContain(keyXml, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(plaintext!), result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(protectedPayload!), result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("master material", result.Message, StringComparison.Ordinal);
    }
}

internal sealed class SlowKeyManager : IKeyManager
{
    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate)
    {
        Thread.Sleep(250);
        return null!;
    }

    public IReadOnlyCollection<IKey> GetAllKeys() => [];

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class NullKeyManager : IKeyManager
{
    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) => null!;

    public IReadOnlyCollection<IKey> GetAllKeys() => [];

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class ThrowingObservationKeyManager : IKeyManager
{
    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) =>
        new TestKey(Guid.NewGuid(), activationDate, expirationDate);

    public IReadOnlyCollection<IKey> GetAllKeys() => new ThrowingKeyCollection();

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class ThrowingKeyCollection : IReadOnlyCollection<IKey>
{
    public int Count => 1;

    public IEnumerator<IKey> GetEnumerator() => throw new InvalidOperationException("collection failure");

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class TestKey : IKey
{
    public TestKey(Guid keyId, DateTimeOffset activationDate, DateTimeOffset expirationDate)
    {
        KeyId = keyId;
        CreationDate = activationDate;
        ActivationDate = activationDate;
        ExpirationDate = expirationDate;
    }

    public DateTimeOffset ActivationDate { get; }

    public DateTimeOffset CreationDate { get; }

    public DateTimeOffset ExpirationDate { get; }

    public bool IsRevoked => false;

    public Guid KeyId { get; }

    public Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.IAuthenticatedEncryptorDescriptor Descriptor => null!;

    public Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.IAuthenticatedEncryptor CreateEncryptor() => null!;
}
