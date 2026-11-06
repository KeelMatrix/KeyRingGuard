using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.Tests;

// Shared public-API probes also compiled by the isolated PackageReference consumer.
internal static class CancellationLifetimeProbe
{
    private static readonly TimeSpan JoinBound = TimeSpan.FromSeconds(8);

    internal static IEnumerable<object[]> StopCases(bool throwing = false)
    {
        foreach (var site in new[] { "primary", "recreated", "secondary", "control-primary", "control-secondary", "manager", "rotation" })
            foreach (var asynchronous in new[] { false, true })
                foreach (var cancel in new[] { false, true })
                    foreach (var handler in throwing ? new[] { "throw" } : site is "primary" or "manager" or "rotation" ? new[] { "none", "fast", "blocked" } : new[] { "blocked" })
                    {
                        yield return [site, asynchronous, cancel, handler];
                    }
    }

    internal static async Task RunAllAsync(bool throwing)
    {
        var count = 0;
        foreach (var row in StopCases(throwing))
        {
            await StopAsync((string)row[0], (bool)row[1], (bool)row[2], (string)row[3]).ConfigureAwait(false);
            count++;
        }
        if (throwing) { await MultipleHandlersAsync().ConfigureAwait(false); count++; }
        else
        {
            foreach (var size in new[] { 31, 32, 33 })
                foreach (var operationFirst in new[] { false, true })
                {
                    await RetentionAsync(size, operationFirst).ConfigureAwait(false);
                    count++;
                }
            foreach (var cancel in new[] { false, true }) { await CooperativeAsync(cancel).ConfigureAwait(false); count++; }
            foreach (var afterDeadline in new[] { false, true }) { await StopPriorityAsync(afterDeadline).ConfigureAwait(false); count++; }
            foreach (var operationFirst in new[] { false, true }) { await ConcurrentStopsAsync(operationFirst).ConfigureAwait(false); count++; }
            foreach (var order in new[] { "removed", "concurrent", "after", "removing" })
                foreach (var cancel in new[] { false, true })
                {
                    await RegistrationAsync(order, cancel).ConfigureAwait(false);
                    count++;
                }
            await CompletionRacesAsync().ConfigureAwait(false);
            await SealedStagesAsync().ConfigureAwait(false);
            await SnapshotBoundaryAsync(false).ConfigureAwait(false);
            await SnapshotBoundaryAsync(true).ConfigureAwait(false);
            count += 4;
        }
        Console.WriteLine($"PACKAGE_CANCELLATION: PASS cases={count} throwing={throwing}");
    }

    internal static async Task StopAsync(string site, bool asynchronous, bool cancel, string handler)
    {
        using var caller = new CancellationTokenSource();
        using var handlerEntered = new ManualResetEventSlim();
        using var handlerRelease = new ManualResetEventSlim();
        var entered = Signal();
        var operationRelease = Signal();
        var operationFinished = Signal();
        var created = 0;
        var disposed = 0;
        var handlerCalls = 0;
        CancellationToken received = default;
        Task? cancellation = null;
        var timeout = cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250);

        ProbeProvider Provider() { Interlocked.Increment(ref created); return new(() => Interlocked.Increment(ref disposed)); }
        async Task Wait(CancellationToken token)
        {
            received = token;
            if (handler != "none")
            {
                token.Register(() =>
                {
                    Interlocked.Increment(ref handlerCalls);
                    handlerEntered.Set();
                    if (handler == "blocked") { handlerRelease.Wait(CancellationToken.None); }
                    if (handler == "throw") { throw new InvalidOperationException("notification-secret"); }
                });
            }

            entered.TrySetResult();
            try { await operationRelease.Task.ConfigureAwait(false); }
            finally { operationFinished.TrySetResult(); }
        }

        var calls = 0;
        KeyRingProviderFactory Factory(string route, KeyRingBoundary boundary)
        {
            async Task<IDataProtectionProvider> Create(CancellationToken token)
            {
                var match = site == route || (site == "recreated" && route == "primary" && Interlocked.Increment(ref calls) == 2);
                if (match) { await Wait(token).ConfigureAwait(false); }
                return Provider();
            }
            return asynchronous
                ? new(Create, timeout, null, boundary)
                : new(token => Create(token).GetAwaiter().GetResult(), timeout, null, boundary);
        }

        var boundary = new KeyRingBoundary();
        var primary = Factory("primary", boundary);
        var secondary = Factory("secondary", boundary);
        Task<KeyRingVerificationResult> verification;
        if (site is "manager" or "rotation")
        {
            async Task<IKeyManager> Manager(CancellationToken token)
            {
                if (site == "manager") { await Wait(token).ConfigureAwait(false); }
                return new ProbeManager();
            }
            Task<IKey> Rotate(IKeyManager _, DateTimeOffset __, DateTimeOffset ___, CancellationToken token)
            {
                if (!asynchronous) { Wait(token).GetAwaiter().GetResult(); return Task.FromException<IKey>(new InvalidOperationException()); }
                return RotateAsync(token);
            }
            async Task<IKey> RotateAsync(CancellationToken token)
            {
                await Wait(token).ConfigureAwait(false);
                throw new InvalidOperationException();
            }
            primary = asynchronous
                ? new(_ => Task.FromResult<IDataProtectionProvider>(Provider()), timeout, Manager, Rotate)
                : new(_ => Provider(), timeout, token => Manager(token).GetAwaiter().GetResult(), Rotate);
            verification = KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, primary, cancellationToken: caller.Token);
        }
        else if (site.StartsWith("control", StringComparison.Ordinal))
        {
            verification = KeyRingVerifier.VerifyWithIsolationControlAsync(KeyRingScenario.ApplicationIsolation,
                primary, secondary, new(Factory("control-primary", boundary), Factory("control-secondary", boundary)), caller.Token);
        }
        else
        {
            verification = KeyRingVerifier.VerifyAsync(site == "secondary" ? KeyRingScenario.ReplicaSharing : KeyRingScenario.RestartContinuity,
                primary, secondary, caller.Token);
        }

        try
        {
            await entered.Task.WaitAsync(JoinBound).ConfigureAwait(false);
            if (cancel)
            {
                cancellation = Task.Factory.StartNew(() => caller.Cancel(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            var result = await verification.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Require(result.FailureKind == (cancel ? KeyRingFailureKind.Canceled : KeyRingFailureKind.Timeout), "Stop classification");
            Require(received.IsCancellationRequested, "Active operation token must be signaled before the result");
            Require(Volatile.Read(ref disposed) == 0, "Active operation retains every provider");
            Require(!result.Message.Contains("secret", StringComparison.Ordinal), "Sanitized notification failure");
            if (handler != "none") { Require(handlerEntered.Wait(JoinBound, CancellationToken.None), "Handler dispatch"); }
            if (cancellation is not null) { await cancellation.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        }
        finally
        {
            handlerRelease.Set();
            operationRelease.TrySetResult();
            await operationFinished.Task.WaitAsync(JoinBound).ConfigureAwait(false);
            if (cancellation is not null) { await cancellation.WaitAsync(JoinBound).ConfigureAwait(false); }
            await verification.WaitAsync(JoinBound).ConfigureAwait(false);
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == Volatile.Read(ref created), JoinBound), "Late exactly-once cleanup");
        }
        Require(handlerCalls == (handler == "none" ? 0 : 1), "Exactly one notification");
    }

    internal static async Task RetentionAsync(int count, bool operationFirst)
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 80), completionPorts);
        using var handlerRelease = new ManualResetEventSlim();
        var operationRelease = Signal();
        var tokens = new List<CancellationToken>();
        var entered = 0;
        var handlersEnded = 0;
        var operationsEnded = 0;
        var disposed = 0;
        var factory = new KeyRingProviderFactory(async token =>
        {
            lock (tokens) { tokens.Add(token); }
            token.Register(() => { handlerRelease.Wait(CancellationToken.None); Interlocked.Increment(ref handlersEnded); });
            Interlocked.Increment(ref entered);
            await operationRelease.Task.ConfigureAwait(false);
            Interlocked.Increment(ref operationsEnded);
            return (IDataProtectionProvider)new ProbeProvider(() => Interlocked.Increment(ref disposed));
        }, TimeSpan.FromMilliseconds(750));
        var tasks = Enumerable.Range(0, count).Select(_ => KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory)).ToArray();
        try
        {
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref entered) == Math.Min(32, count), JoinBound), "Retained capacity entry");
            var results = await Task.WhenAll(tasks).WaitAsync(JoinBound).ConfigureAwait(false);
            Require(results.All(r => r.FailureKind == KeyRingFailureKind.Timeout), "Retained timeout results");
            Require(tokens.All(t => t.IsCancellationRequested), "Every retained operation signaled with slots full");
            if (operationFirst)
            {
                operationRelease.TrySetResult();
                Require(SpinWait.SpinUntil(() => Volatile.Read(ref operationsEnded) == Math.Min(32, count), JoinBound), "Operation-only release");
            }
            else
            {
                handlerRelease.Set();
                Require(SpinWait.SpinUntil(() => Volatile.Read(ref handlersEnded) == Math.Min(32, count), JoinBound), "Notification-only release");
            }
            Require(disposed == 0, "Both work owners must finish before cleanup");
            foreach (var token in tokens) { Require(token.WaitHandle.WaitOne(0), "Live source retained through either release order"); }
            var extraCalls = 0;
            var extra = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity,
                new KeyRingProviderFactory(_ => { Interlocked.Increment(ref extraCalls); return new ProbeProvider(); }, TimeSpan.FromMilliseconds(100))).ConfigureAwait(false);
            Require(count == 31 ? extra.Succeeded && extraCalls == 2 : extra.FailureKind == KeyRingFailureKind.Timeout && extraCalls == 0, "31/32/33 lifecycle admission");
            Require(entered == Math.Min(count, 32), "32 callback limit and no 33rd provider");
        }
        finally
        {
            try
            {
                operationRelease.TrySetResult();
                handlerRelease.Set();
                await Task.WhenAll(tasks).WaitAsync(JoinBound).ConfigureAwait(false);
                Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == Math.Min(count, 32) && Volatile.Read(ref handlersEnded) == Math.Min(count, 32), JoinBound), "Retained work drains exactly once");
            }
            finally { ThreadPool.SetMinThreads(workers, completionPorts); }
        }
        await RecoveryAsync().ConfigureAwait(false);
    }

    internal static async Task CooperativeAsync(bool cancel)
    {
        using var caller = new CancellationTokenSource();
        var escape = Signal();
        var entered = 0;
        var finished = 0;
        var disposed = 0;
        var factory = new KeyRingProviderFactory(async token =>
        {
            var delay = Task.Delay(Timeout.InfiniteTimeSpan, token);
            Interlocked.Increment(ref entered);
            try { await (await Task.WhenAny(delay, escape.Task).ConfigureAwait(false)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { Interlocked.Increment(ref finished); }
            return (IDataProtectionProvider)new ProbeProvider(() => Interlocked.Increment(ref disposed));
        }, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(750));
        var tasks = Enumerable.Range(0, 32).Select(_ => KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token)).ToArray();
        try
        {
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref entered) == 32, JoinBound), "All 32 callback slots occupied");
            if (cancel) { caller.Cancel(); }
            var results = await Task.WhenAll(tasks).WaitAsync(JoinBound).ConfigureAwait(false);
            Require(results.All(r => r.FailureKind == (cancel ? KeyRingFailureKind.Canceled : KeyRingFailureKind.Timeout)), "Cooperative stop verdicts");
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref finished) == 32 && Volatile.Read(ref disposed) == 32, JoinBound), "Cooperative completion without escape gate");
        }
        finally
        {
            escape.TrySetResult();
            await Task.WhenAll(tasks).WaitAsync(JoinBound).ConfigureAwait(false);
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref finished) == 32 && Volatile.Read(ref disposed) == 32, JoinBound), "Cooperative drain");
        }
        // A second batch takes total use beyond 32 without relying on a cleanup sleep.
        for (var i = 0; i < 8; i++) { await RecoveryAsync().ConfigureAwait(false); }
    }

    internal static async Task RegistrationAsync(string order, bool cancel)
    {
        using var caller = new CancellationTokenSource();
        using var handlerRelease = new ManualResetEventSlim();
        using var handlerEntered = new ManualResetEventSlim();
        var ready = Signal();
        var register = Signal();
        var operationRelease = Signal();
        var finished = Signal();
        var disposed = 0;
        var calls = 0;
        CancellationToken received = default;
        var factory = new KeyRingProviderFactory(async token =>
        {
            received = token;
            CancellationTokenRegistration registration = default;
            void Handler() { Interlocked.Increment(ref calls); handlerEntered.Set(); handlerRelease.Wait(CancellationToken.None); }
            try
            {
                if (order is "removed" or "removing")
                {
                    registration = token.Register(Handler);
                    if (order == "removed") { registration.Dispose(); }
                }
                ready.TrySetResult();
                if (order is "concurrent" or "after")
                {
                    await register.Task.ConfigureAwait(false);
                    registration = token.Register(Handler);
                }
                if (order == "removing")
                {
                    Require(handlerEntered.Wait(JoinBound, CancellationToken.None), "Executing registration");
                    registration.Dispose();
                }
                await operationRelease.Task.ConfigureAwait(false);
                Require(token.WaitHandle.WaitOne(0), "Source not disposed while operation registers or unregisters");
                return (IDataProtectionProvider)new ProbeProvider(() => Interlocked.Increment(ref disposed));
            }
            finally { finished.TrySetResult(); }
        }, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250));
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
        try
        {
            await ready.Task.WaitAsync(JoinBound).ConfigureAwait(false);
            if (order == "concurrent") { register.TrySetResult(); }
            if (cancel) { caller.Cancel(); }
            var result = await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Require(result.FailureKind == (cancel ? KeyRingFailureKind.Canceled : KeyRingFailureKind.Timeout) && received.IsCancellationRequested, "Registration ordering result and active signal");
            register.TrySetResult();
            if (order != "removed") { Require(handlerEntered.Wait(JoinBound, CancellationToken.None), "Late/active registration entered"); }
            Require(disposed == 0, "Inline registration remains operation-owned");
        }
        finally
        {
            register.TrySetResult();
            handlerRelease.Set();
            operationRelease.TrySetResult();
            await finished.Task.WaitAsync(JoinBound).ConfigureAwait(false);
            await task.WaitAsync(JoinBound).ConfigureAwait(false);
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, JoinBound), "Registration exactly-once drain");
        }
        Require(calls == (order == "removed" ? 0 : 1), "Registration count");
    }

    internal static async Task CompletionRacesAsync()
    {
        foreach (var stopFirst in new[] { false, true })
            for (var repeat = 0; repeat < 24; repeat++)
            {
                using var caller = new CancellationTokenSource();
                var ready = Signal();
                var release = Signal();
                var disposed = 0;
                var callbacks = 0;
                var nextStage = 0;
                var calls = 0;
                var factory = new KeyRingProviderFactory(async token =>
                {
                    if (Interlocked.Increment(ref calls) != 1) { Interlocked.Increment(ref nextStage); return new ProbeProvider(() => Interlocked.Increment(ref disposed)); }
                    token.Register(() => Interlocked.Increment(ref callbacks));
                    ready.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return (IDataProtectionProvider)new ProbeProvider(() => Interlocked.Increment(ref disposed));
                }, TimeSpan.FromMilliseconds(250));
                var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
                try
                {
                    await ready.Task.WaitAsync(JoinBound).ConfigureAwait(false);
                    if (stopFirst) { caller.Cancel(); release.TrySetResult(); }
                    else { release.TrySetResult(); caller.Cancel(); }
                    var result = await task.WaitAsync(JoinBound).ConfigureAwait(false);
                    Require(result.FailureKind == KeyRingFailureKind.Canceled || (!stopFirst && result.Succeeded), "Completion/stop decision ordering");
                    if (stopFirst) { Require(nextStage == 0, "No next stage after accepted stop"); }
                }
                finally
                {
                    release.TrySetResult();
                    await task.WaitAsync(JoinBound).ConfigureAwait(false);
                    Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == Volatile.Read(ref calls), JoinBound), "Race exactly-once teardown");
                }
                Require(callbacks <= 1, "One winning dispatch");
            }
        await RecoveryAsync().ConfigureAwait(false);
    }

    internal static async Task SealedStagesAsync()
    {
        using var caller = new CancellationTokenSource();
        var ready = Signal();
        var release = Signal();
        var calls = 0;
        var firstNotified = 0;
        var disposed = 0;
        CancellationToken first = default;
        var factory = new KeyRingProviderFactory(async token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                first = token;
                token.Register(() => Interlocked.Increment(ref firstNotified));
            }
            else { ready.TrySetResult(); await release.Task.ConfigureAwait(false); }
            return (IDataProtectionProvider)new ProbeProvider(() => Interlocked.Increment(ref disposed));
        }, TimeSpan.FromSeconds(5));
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
        try
        {
            await ready.Task.WaitAsync(JoinBound).ConfigureAwait(false);
            caller.Cancel();
            Require((await task.WaitAsync(JoinBound).ConfigureAwait(false)).FailureKind == KeyRingFailureKind.Canceled, "Later stage canceled");
            Require(!first.IsCancellationRequested && firstNotified == 0, "Successful stage sealed before progression");
        }
        finally
        {
            release.TrySetResult();
            await task.WaitAsync(JoinBound).ConfigureAwait(false);
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 2, JoinBound), "Sealed stage cleanup");
        }
    }

    internal static async Task StopPriorityAsync(bool afterDeadline)
    {
        using var caller = new CancellationTokenSource();
        using var notificationRelease = new ManualResetEventSlim();
        using var notificationEntered = new ManualResetEventSlim();
        var entered = Signal();
        var operationRelease = Signal();
        var disposed = 0;
        var notifications = 0;
        CancellationToken received = default;
        var factory = new KeyRingProviderFactory(async token =>
        {
            received = token;
            token.Register(() =>
            {
                Interlocked.Increment(ref notifications);
                notificationEntered.Set();
                notificationRelease.Wait(CancellationToken.None);
            });
            entered.TrySetResult();
            await operationRelease.Task.ConfigureAwait(false);
            return (IDataProtectionProvider)new ProbeProvider(() => Interlocked.Increment(ref disposed));
        }, TimeSpan.FromMilliseconds(250));
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
        try
        {
            await entered.Task.WaitAsync(JoinBound).ConfigureAwait(false);
            if (!afterDeadline) { caller.Cancel(); }
            var result = await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Require(result.FailureKind == (afterDeadline ? KeyRingFailureKind.Timeout : KeyRingFailureKind.Canceled), "Caller before/after deadline arbitration");
            Require(notificationEntered.Wait(JoinBound, CancellationToken.None) && received.IsCancellationRequested, "Winning dispatch retained");
            if (afterDeadline) { caller.Cancel(); }
            // The other input arrives while notification and operation still own the source.
            // Its deadline is real, rather than another full timeout after queue admission.
            await Task.Delay(300).ConfigureAwait(false);
            Require(notifications == 1 && received.WaitHandle.WaitOne(0) && disposed == 0, "Repeated stop cannot replace dispatch or dispose live source");
        }
        finally
        {
            notificationRelease.Set();
            operationRelease.TrySetResult();
            await task.WaitAsync(JoinBound).ConfigureAwait(false);
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, JoinBound), "Repeated stop drains");
        }
    }

    internal static async Task ConcurrentStopsAsync(bool operationFirst)
    {
        using var caller = new CancellationTokenSource();
        using var handlerEntered = new ManualResetEventSlim();
        using var handlerRelease = new ManualResetEventSlim();
        var operationRelease = Signal();
        var operationEnded = Signal();
        var disposed = 0;
        var notifications = 0;
        CancellationToken received = default;
        var factory = new KeyRingProviderFactory(async token =>
        {
            received = token;
            token.Register(() =>
            {
                // A deadline dispatch itself requests caller cancellation, racing its
                // owner relay while the first notification task is still unfinished.
                caller.Cancel();
                Interlocked.Increment(ref notifications);
                if (operationFirst) { operationRelease.TrySetResult(); }
                handlerEntered.Set();
                handlerRelease.Wait(CancellationToken.None);
            });
            await operationRelease.Task.ConfigureAwait(false);
            operationEnded.TrySetResult();
            return (IDataProtectionProvider)new ProbeProvider(() => Interlocked.Increment(ref disposed));
        }, TimeSpan.FromMilliseconds(250));
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
        try
        {
            var result = await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Require(result.FailureKind is KeyRingFailureKind.Timeout or KeyRingFailureKind.Canceled, "Racing input stop result");
            Require(handlerEntered.Wait(JoinBound, CancellationToken.None) && received.IsCancellationRequested && caller.IsCancellationRequested, "Both stop inputs delivered");
            if (operationFirst) { await operationEnded.Task.WaitAsync(JoinBound).ConfigureAwait(false); }
            Require(notifications == 1 && disposed == 0 && received.WaitHandle.WaitOne(0), "First unfinished notification task retains ownership");
        }
        finally
        {
            handlerRelease.Set();
            operationRelease.TrySetResult();
            await operationEnded.Task.WaitAsync(JoinBound).ConfigureAwait(false);
            await task.WaitAsync(JoinBound).ConfigureAwait(false);
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, JoinBound), "Concurrent stop exactly-once cleanup");
        }
    }

    internal static async Task SnapshotBoundaryAsync(bool fail)
    {
        using var caller = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var rotations = 0;
        var disposed = 0;
        var snapshotEnded = Signal();
        var manager = new ProbeManager(() =>
        {
            entered.Set();
            release.Wait(CancellationToken.None);
            snapshotEnded.TrySetResult();
            if (fail) { throw new InvalidOperationException("snapshot-secret"); }
        });
        var factory = new KeyRingProviderFactory(_ => new ProbeProvider(() => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(5), _ => manager,
            (_, _, _, _) => { Interlocked.Increment(ref rotations); return Task.FromException<IKey>(new InvalidOperationException()); });
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory, cancellationToken: caller.Token);
        try
        {
            Require(entered.Wait(JoinBound, CancellationToken.None), "Snapshot entry");
            caller.Cancel();
            Require((await task.WaitAsync(JoinBound).ConfigureAwait(false)).FailureKind == KeyRingFailureKind.Canceled, "Snapshot cancellation result");
            Require(disposed == 0, "Snapshot still owns provider");
        }
        finally
        {
            release.Set();
            await snapshotEnded.Task.WaitAsync(JoinBound).ConfigureAwait(false);
            await task.WaitAsync(JoinBound).ConfigureAwait(false);
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, JoinBound), "Snapshot cleanup");
        }
        Require(rotations == 0, "No rotation after canceled/failed snapshot");
    }

    private static async Task MultipleHandlersAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var caller = new CancellationTokenSource();
            var ready = Signal();
            var escape = Signal();
            var notified = Signal();
            var disposed = 0;
            var nested = new List<KeyRingFailureKind>();
            var factory = new KeyRingProviderFactory(async token =>
            {
                var cooperative = Task.Delay(Timeout.InfiniteTimeSpan, token);
                token.Register(() =>
                {
                    var result = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity,
                        new KeyRingProviderFactory(_ => new ProbeProvider(), TimeSpan.FromMilliseconds(100))).GetAwaiter().GetResult();
                    lock (nested) { nested.Add(result.FailureKind); }
                    notified.TrySetResult();
                });
                token.Register(() => throw new InvalidOperationException("handler-secret"));
                ready.TrySetResult();
                try { await (await Task.WhenAny(cooperative, escape.Task).ConfigureAwait(false)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return (IDataProtectionProvider)new ProbeProvider(() => Interlocked.Increment(ref disposed));
            }, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250));
            var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
            try
            {
                await ready.Task.WaitAsync(JoinBound).ConfigureAwait(false);
                if (cancel) { caller.Cancel(); }
                Require((await task.WaitAsync(JoinBound).ConfigureAwait(false)).FailureKind == (cancel ? KeyRingFailureKind.Canceled : KeyRingFailureKind.Timeout), "Throwing notification verdict");
                await notified.Task.WaitAsync(JoinBound).ConfigureAwait(false);
                Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, JoinBound), "Throw then cooperative notification and cleanup");
                Require(nested.SequenceEqual(new[] { KeyRingFailureKind.InvalidScenario }), "Handler flowed-context reentry");
            }
            finally
            {
                escape.TrySetResult();
                await task.WaitAsync(JoinBound).ConfigureAwait(false);
                Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, JoinBound), "Multiple notification drain");
            }
        }
    }

    private static async Task RecoveryAsync()
    {
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity,
            new KeyRingProviderFactory(_ => new ProbeProvider(), TimeSpan.FromSeconds(3))).WaitAsync(JoinBound).ConfigureAwait(false);
        Require(result.Succeeded, "Post-drain capacity recovery");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class ProbeProvider(Action? dispose = null) : IDataProtectionProvider, IDisposable
    {
        public IDataProtector CreateProtector(string purpose) => new Protector();
        public void Dispose() => dispose?.Invoke();
        private sealed class Protector : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => this;
            public byte[] Protect(byte[] plaintext) => (byte[])plaintext.Clone();
            public byte[] Unprotect(byte[] protectedData) => (byte[])protectedData.Clone();
        }
    }

    private sealed class ProbeManager(Action? snapshot = null) : IKeyManager
    {
        public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) => throw new NotSupportedException();
        public IReadOnlyCollection<IKey> GetAllKeys() { snapshot?.Invoke(); return []; }
        public CancellationToken GetCacheExpirationToken() => CancellationToken.None;
        public void RevokeKey(Guid keyId, string? reason = null) => throw new NotSupportedException();
        public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason = null) => throw new NotSupportedException();
    }
}
