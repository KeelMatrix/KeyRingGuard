namespace KeelMatrix.KeyRingGuard.UnitTests;

public sealed partial class VerifierFailureTests
{
    [Fact]
    public async Task CanceledQueuedSynchronousFactoryDoesNotRunAfterCapacityIsReleased()
    {
        using var release = new ManualResetEventSlim();
        var started = 0;
        var lateInvocations = 0;
        var blockingFactories = Enumerable.Range(0, 32)
            .Select(_ => new KeyRingProviderFactory(
                _ =>
                {
                    Interlocked.Increment(ref started);
                    release.Wait(CancellationToken.None);
                    return new FakeProvider();
                },
                TimeSpan.FromSeconds(5)))
            .ToArray();
        var running = blockingFactories
            .Select(factory => KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory))
            .ToArray();
        var queuedFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref lateInvocations);
                return new FakeProvider();
            },
            TimeSpan.FromMilliseconds(40));

        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started) == 32, TimeSpan.FromSeconds(5)));
            var queuedResult = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, queuedFactory);

            Assert.Equal(KeyRingFailureKind.Timeout, queuedResult.FailureKind);
            release.Set();
            await Task.WhenAll(running);
            Assert.Equal(0, Volatile.Read(ref lateInvocations));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(running);
        }
    }

    [Fact]
    public async Task SaturatedSynchronousProtectsAndQueuedAdmissionReportTimeout()
    {
        using var release = new ManualResetEventSlim();
        var started = 0;
        var blockerFactory = new KeyRingProviderFactory(
            _ => new FakeProvider(protect: value =>
            {
                Interlocked.Increment(ref started);
                release.Wait(CancellationToken.None);
                return value;
            }), TimeSpan.FromMilliseconds(500));
        var blockers = Enumerable.Range(0, 32)
            .Select(_ => KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, blockerFactory)).ToArray();
        var queuedCalls = 0;
        var targetFactory = new KeyRingProviderFactory(_ =>
        {
            Interlocked.Increment(ref queuedCalls);
            return new FakeProvider();
        }, TimeSpan.FromMilliseconds(40));

        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started) == 32, TimeSpan.FromSeconds(3)));
            Assert.Equal(KeyRingFailureKind.Timeout, (await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, targetFactory)).FailureKind);
            var results = await Task.WhenAll(blockers).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(results, result => Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind));
            Assert.Equal(0, queuedCalls);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(blockers);
        }
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task ConcurrentRotationsCompleteBelowAtAndAboveSchedulerCapacity(int concurrentVerifications)
    {
        var factories = Enumerable.Range(0, concurrentVerifications)
            .Select(_ => CreateFastRotationFactory())
            .ToArray();

        var results = await Task.WhenAll(
                factories.Select(factory => KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Message));
    }

    [Fact]
    public async Task AFailedRotationDoesNotStarveOtherRotationsAtCapacity()
    {
        var factories = Enumerable.Range(0, 32)
            .Select(index => CreateFastRotationFactory(index == 0))
            .ToArray();

        var results = await Task.WhenAll(
                factories.Select(factory => KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(KeyRingFailureKind.RotationFailure, results[0].FailureKind);
        Assert.Equal(31, results.Count(result => result.Succeeded));
    }
}
