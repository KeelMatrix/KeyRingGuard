using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard.UnitTests;

internal sealed class ReentryProbe
{
    private int _triggered;

    internal ConcurrentBag<KeyRingVerificationResult> NestedResults { get; } = new();

    internal int NestedFactoryInvocations;

    internal void TriggerSync(CancellationToken cancellationToken) =>
        TriggerAsync(cancellationToken).GetAwaiter().GetResult();

    internal async Task TriggerAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _triggered, 1) != 0)
        {
            return;
        }

        var nestedFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref NestedFactoryInvocations);
                return new FakeProvider();
            },
            TimeSpan.FromSeconds(1));
        NestedResults.Add(
            await KeyRingVerifier.VerifyAsync(
                    KeyRingScenario.RestartContinuity,
                    nestedFactory,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));
    }
}

internal sealed class NestedOutcomeProbe
{
    internal ConcurrentBag<KeyRingVerificationResult> Results { get; } = new();

    internal int NestedFactoryInvocations;

    internal async Task RunAsync(CancellationToken _)
    {
        var faultedFactory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(token =>
            {
                Interlocked.Increment(ref NestedFactoryInvocations);
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("nested fault");
            }),
            TimeSpan.FromSeconds(1));
        Results.Add(await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, faultedFactory, cancellationToken: CancellationToken.None).ConfigureAwait(false));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref NestedFactoryInvocations);
                return new FakeProvider();
            },
            TimeSpan.FromSeconds(1));
        Results.Add(
            await KeyRingVerifier.VerifyAsync(
                    KeyRingScenario.RestartContinuity,
                    canceledFactory,
                    cancellationToken: canceled.Token)
                .ConfigureAwait(false));

        var timedOutFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref NestedFactoryInvocations);
                Thread.Sleep(100);
                return new FakeProvider();
            },
            TimeSpan.FromMilliseconds(10));
        Results.Add(await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, timedOutFactory, cancellationToken: CancellationToken.None).ConfigureAwait(false));
    }
}
