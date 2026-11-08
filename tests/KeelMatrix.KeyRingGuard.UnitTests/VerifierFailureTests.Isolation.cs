using System.Security.Cryptography;

namespace KeelMatrix.KeyRingGuard.UnitTests;

public sealed partial class VerifierFailureTests
{
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
    public async Task PurposeIsolationDoesNotTreatAnArbitraryCrossBoundaryCryptographicExceptionAsProof()
    {
        var factory = new KeyRingProviderFactory(
            _ => CreatePurposeTaggedProvider(),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
        Assert.Contains("standard ASP.NET Core persisted-protector contract", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplicationIsolationDoesNotTreatAnArbitraryCrossBoundaryCryptographicExceptionAsProof()
    {
        var boundary = new KeyRingBoundary();
        var firstFactory = new KeyRingProviderFactory(
            _ => CreateApplicationTaggedProvider(1),
            TimeSpan.FromSeconds(1),
            null,
            boundary);
        var secondFactory = new KeyRingProviderFactory(
            _ => CreateApplicationTaggedProvider(2),
            TimeSpan.FromSeconds(1),
            null,
            boundary);

        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            firstFactory,
            secondFactory,
            new KeyRingIsolationControl(
                new KeyRingProviderFactory(_ => CreateApplicationTaggedProvider(0), TimeSpan.FromSeconds(1), null, boundary),
                new KeyRingProviderFactory(_ => CreateApplicationTaggedProvider(0), TimeSpan.FromSeconds(1), null, boundary)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
        Assert.Contains("standard ASP.NET Core persisted-protector contract", result.Message, StringComparison.Ordinal);
    }

    private static FakeProvider CreatePurposeTaggedProvider() =>
        new(
            createProtector: purpose =>
            {
                var tag = purpose.EndsWith(".Alternate", StringComparison.Ordinal) ? (byte)2 : (byte)1;
                return CreateTaggedProtector(tag);
            });

    private static FakeProvider CreateApplicationTaggedProvider(byte tag) =>
        new(createProtector: _ => CreateTaggedProtector(tag));

    private static FakeProtector CreateTaggedProtector(byte tag) =>
        new(
            payload => [tag, .. payload],
            protectedPayload =>
            {
                if (protectedPayload.Length > 0 && protectedPayload[0] == tag)
                {
                    return protectedPayload[1..];
                }

                throw new CryptographicException("unrelated cross-boundary provider failure");
            });
}
