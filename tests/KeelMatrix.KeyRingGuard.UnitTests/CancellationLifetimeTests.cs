using KeelMatrix.KeyRingGuard.Tests;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

[Collection("Provider lifecycle")]
public sealed class CancellationLifetimeTests
{
    public static IEnumerable<object[]> StopCases => CancellationLifetimeProbe.StopCases();

    [Theory]
    [MemberData(nameof(StopCases))]
    public Task ActiveStopSignalsWithoutJoiningHandlers(string site, bool asynchronous, bool cancel, string handler) =>
        CancellationLifetimeProbe.StopAsync(site, asynchronous, cancel, handler);

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(31, true)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    public Task RetainBothWorkOwners(int count, bool operationFirst) => CancellationLifetimeProbe.RetentionAsync(count, operationFirst);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AllCooperativeSlotsReceiveCancellation(bool cancel) => CancellationLifetimeProbe.CooperativeAsync(cancel);

    [Theory]
    [InlineData("removed", false)]
    [InlineData("removed", true)]
    [InlineData("concurrent", false)]
    [InlineData("concurrent", true)]
    [InlineData("after", false)]
    [InlineData("after", true)]
    [InlineData("removing", false)]
    [InlineData("removing", true)]
    public Task RegistrationsRemainOperationOwned(string order, bool cancel) => CancellationLifetimeProbe.RegistrationAsync(order, cancel);

    [Fact]
    public Task CompletionStopAndTeardownRaces() => CancellationLifetimeProbe.CompletionRacesAsync();

    [Fact]
    public Task CompletedStagesCannotBeCanceledLater() => CancellationLifetimeProbe.SealedStagesAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SnapshotCannotAdvanceToCanceledRotation(bool fail) => CancellationLifetimeProbe.SnapshotBoundaryAsync(fail);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CallerBeforeAndAfterDeadlineRetainsFirstDispatch(bool afterDeadline) => CancellationLifetimeProbe.StopPriorityAsync(afterDeadline);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RacingStopInputsRetainFirstNotification(bool operationFirst) => CancellationLifetimeProbe.ConcurrentStopsAsync(operationFirst);

    public static IEnumerable<object[]> PreCanceledCases =>
        new[] { "restart", "replica", "rotation", "isolation" }.SelectMany(site =>
            new[] { false, true }.Select(asynchronous => new object[] { site, asynchronous }));

    [Theory]
    [MemberData(nameof(PreCanceledCases))]
    public async Task PreCanceledVerificationNeverInvokesConsumerCode(string site, bool asynchronous)
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var calls = 0;
        var boundary = new KeyRingBoundary();
        IDataProtectionProvider Create(CancellationToken _) { Interlocked.Increment(ref calls); return new FakeProvider(); }
        IKeyManager Manager(CancellationToken _) { Interlocked.Increment(ref calls); throw new InvalidOperationException(); }
        Task<IKey> Rotate(IKeyManager _, DateTimeOffset __, DateTimeOffset ___, CancellationToken token)
        { Interlocked.Increment(ref calls); throw new InvalidOperationException(); }
        var factory = asynchronous
            ? new KeyRingProviderFactory(token => Task.FromResult(Create(token)), TimeSpan.FromSeconds(1), token => Task.FromResult(Manager(token)), Rotate, boundary)
            : new KeyRingProviderFactory(Create, TimeSpan.FromSeconds(1), Manager, Rotate, boundary);
        var scenario = site switch
        {
            "replica" => KeyRingScenario.ReplicaSharing,
            "rotation" => KeyRingScenario.RotationContinuity,
            _ => KeyRingScenario.RestartContinuity
        };
        var result = site == "isolation"
            ? await KeyRingVerifier.VerifyWithIsolationControlAsync(KeyRingScenario.ApplicationIsolation, factory, factory, new(factory, factory), caller.Token)
            : await KeyRingVerifier.VerifyAsync(scenario, factory, factory, caller.Token);
        Assert.Equal(KeyRingFailureKind.Canceled, result.FailureKind);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("protector", false)]
    [InlineData("protector", true)]
    [InlineData("protect", false)]
    [InlineData("protect", true)]
    [InlineData("unprotect", false)]
    [InlineData("unprotect", true)]
    [InlineData("observation", false)]
    [InlineData("observation", true)]
    public async Task OrdinaryCallbacksRemainOwnedThroughLateFault(string stage, bool cancel)
    {
        using var caller = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var disposed = 0;
        byte[] Block(byte[] value)
        {
            entered.Set();
            release.Wait();
            finished.TrySetResult();
            throw new InvalidOperationException("late-operation-secret");
        }
        var factory = new KeyRingProviderFactory(_ =>
        {
            Interlocked.Increment(ref created);
            return new FakeProvider(
                createProtector: stage == "protector" ? _ => { Block([]); return new FakeProtector(null, v => v); }
            : null,
                protect: stage == "protect" ? Block : null,
                unprotect: stage == "unprotect" ? Block : null,
                dispose: () => Interlocked.Increment(ref disposed));
        }, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250));
        if (stage == "observation")
        {
            var manager = new BlockingObservationManager(() => Block([]));
            factory = new(_ => { Interlocked.Increment(ref created); return new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)); },
                cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250), _ => manager,
                (m, activation, expiration, _) => Task.FromResult(m.CreateNewKey(activation, expiration)));
        }
        var task = KeyRingVerifier.VerifyAsync(stage == "observation" ? KeyRingScenario.RotationContinuity : KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (cancel) { caller.Cancel(); }
            var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(cancel ? KeyRingFailureKind.Canceled : KeyRingFailureKind.Timeout, result.FailureKind);
            Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
            Assert.Equal(stage == "unprotect" ? 1 : 0, disposed); // Restart A retired before B's read.
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == Volatile.Read(ref created), TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class BlockingObservationManager(Action observe) : IKeyManager
    {
        private int _reads;
        public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) => new TestKey(Guid.NewGuid(), activationDate, expirationDate);
        public IReadOnlyCollection<IKey> GetAllKeys() { if (++_reads > 1) { observe(); } return []; }
        public CancellationToken GetCacheExpirationToken() => CancellationToken.None;
        public void RevokeKey(Guid keyId, string? reason = null) => throw new NotSupportedException();
        public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason = null) => throw new NotSupportedException();
    }
}
