using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard.UnitTests;

[Collection("Provider lifecycle")]
public sealed partial class VerifierFailureTests
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
    public async Task AsyncProviderFailureIsCategorizedWithoutExceptionText()
    {
        const string secret = "<async key secret> provider-path=do-not-emit";
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
                Task.FromException<IDataProtectionProvider>(new InvalidOperationException(secret))),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
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
