using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Executes provider-neutral Data Protection continuity and isolation scenarios.
/// Isolation rejection is attributed only when the cross-check protector implements the standard
/// ASP.NET Core persisted-protector contract; arbitrary provider exceptions fail closed.
/// </summary>
/// <remarks>
/// This is the authoritative timeout, buffer, error-attribution, cleanup, and ownership contract for the shipping API.
/// Expected canaries and retained ciphertext are private snapshots. Each protection/unprotection callback
/// receives a working copy, and returned bytes are copied inside the callback boundary before later calls.
/// Callbacks may consume their input or reuse output storage on subsequent calls. Concurrent mutation
/// outside a callback's declared lifetime remains consumer-owned and is unsupported.
/// A shared limit admits at most 32 verification lifecycles. Admission waits at most the primary
/// factory's timeout and honors caller cancellation. Each lifecycle retains its reservation until
/// all active operations, cancellation notifications, and provider cleanup finish, even after the caller stops waiting.
/// Provider, key-manager, rotation, and cleanup callbacks share a separate limit of 32 active callbacks.
/// Each operation's configured timeout bounds observation, including scheduler admission. Before a stop
/// result returns, its operation token is signaled; arbitrary cancellation registrations run asynchronously
/// without delaying that result or consuming the primary callback slots. Signaling does not promise that
/// every registration has run or completed. At most one unfinished cancellation dispatch is retained per
/// terminating lifecycle, bounded by the 32 lifecycle reservations; it uses no dedicated notification thread.
/// Successful operations seal their cancellation lifetime before the next scenario stage. A stop that wins
/// that decision ends the scenario; caller cancellation takes priority when observed at the decision.
/// Only that sealed stop cause selects Timeout or Canceled. An independently canceled callback or task
/// receives its operation-specific failure category, without exposing exception text or token details.
/// Arbitrary running user code cannot be interrupted. Providers returned by factories belong to verification.
/// Each distinct disposable provider is cleaned up once, after operations and owned notifications finish.
/// Restart continuity completes A's operation/notification quiescence and retirement before creating B.
/// One primary-factory timeout bounds that transition, including quiescence, scheduler admission, and Dispose.
/// A retirement exception returns ProviderCreation; a stopped retirement returns Timeout or Canceled.
/// None of these outcomes admits B. A transferred retirement attempt continues uncanceled and is never repeated.
/// For non-IDisposable providers, retirement ends verifier use; it cannot force GC or destroy caller-held objects.
/// Return an owning IDisposable adapter when releasing an external scope is necessary before reopening.
/// Replica and isolation providers retain their intentionally simultaneous lifetimes.
/// Final cleanup runs as one bounded batch per lifecycle, including late factory results, and is best effort:
/// exceptions are suppressed, and a blocked provider delays later providers in its batch until it returns.
/// When no operation or notification remains active, the caller waits at most the primary factory's timeout for cleanup.
/// Final-cleanup timeout or exceptions preserve the scenario verdict; caller cancellation during that wait
/// produces <see cref="KeyRingFailureKind.Canceled"/>. An already timed-out/canceled active operation or notification
/// adds no cleanup wait. Cleanup continues without cancellation and retains its reservation until finished.
/// Thus permanently blocked user code can exhaust capacity, but repeated calls cannot accumulate owned
/// providers, cleanup batches, or dedicated execution threads beyond these limits. Duration includes
/// admission, scenario execution, and cleanup actually awaited; it excludes continuing background work.
/// Late operation and notification exceptions are observed without exposing consumer exception text.
/// Registrations created after cancellation can execute inline on their operation's thread and remain
/// owned by that operation. Work escaping a callback's declared task lifetime remains consumer-owned.
/// Callback re-entry on flowed execution context, including cancellation handlers and cleanup, is rejected before admission.
/// Deliberately severed execution-context flow remains unsupported.
/// Rotation requires the standard header, a canonical little-endian key ID, and a non-empty body.
/// Every structurally valid envelope is interpreted canonically: any wrong or stale ID fails adoption,
/// including bytes equal to a distinct key's alternate representation. Malformed envelopes are unsupported.
/// </remarks>
public static class KeyRingVerifier
{
    private const string SharedPurpose = "KeelMatrix.KeyRingGuard.Verification";
    private const string AlternatePurpose = "KeelMatrix.KeyRingGuard.Verification.Alternate";

    /// <summary>
    /// Verifies a scenario using one or two caller-supplied provider factories.
    /// </summary>
    /// <param name="scenario">The immutable scenario description to execute.</param>
    /// <param name="providerFactory">The primary provider factory.</param>
    /// <param name="secondaryProviderFactory">The second factory required by replica and application-isolation scenarios.</param>
    /// <param name="cancellationToken">Cancels admission and waiting for verification operations and cleanup.</param>
    /// <returns>A structured result that never includes key material, canaries, or raw provider exceptions.</returns>
    /// <remarks>
    /// A verification started on a provider, key-manager, rotation, or cleanup callback's own execution flow returns
    /// <see cref="KeyRingFailureKind.InvalidScenario"/> before scheduler admission. The guard follows flowed
    /// execution context; a callback that deliberately severs it with <c>ExecutionContext.SuppressFlow</c>,
    /// <c>ThreadPool.UnsafeQueueUserWorkItem</c>, or a new <see cref="Thread"/> is outside the guard and is
    /// unsupported. The class remarks define lifecycle admission, the shared 32 callback slots,
    /// bounded cleanup observation, and provider ownership.
    /// </remarks>
    public static async Task<KeyRingVerificationResult> VerifyAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory = null,
        CancellationToken cancellationToken = default)
        => await VerifyCoreAsync(
            scenario,
            providerFactory,
            secondaryProviderFactory,
            isolationControl: null,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Verifies a scenario using caller-supplied provider factories and a shared-boundary control.
    /// Application isolation requires a control configured for the same key store and application discriminator
    /// on both sides. Cross-boundary rejection is accepted only from the standard ASP.NET Core persisted
    /// protector contract; an arbitrary provider exception is reported as a failure.
    /// </summary>
    /// <param name="scenario">The immutable scenario description to execute.</param>
    /// <param name="providerFactory">The primary provider factory.</param>
    /// <param name="secondaryProviderFactory">The second factory required by replica and isolation scenarios.</param>
    /// <param name="isolationControl">The same-boundary control required by application isolation.</param>
    /// <param name="cancellationToken">Cancels admission and waiting for verification operations and cleanup.</param>
    /// <returns>A structured result that never includes key material, canaries, or raw provider exceptions.</returns>
    /// <remarks>
    /// This entry point has the same callback re-entry boundary as <see cref="VerifyAsync"/>: a verification
    /// started on a callback's flowed execution context returns <see cref="KeyRingFailureKind.InvalidScenario"/>
    /// before scheduler admission, while deliberately severed execution-context flow is outside the guard.
    /// </remarks>
    public static Task<KeyRingVerificationResult> VerifyWithIsolationControlAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory,
        KeyRingIsolationControl isolationControl,
        CancellationToken cancellationToken)
        => VerifyCoreAsync(scenario, providerFactory, secondaryProviderFactory, isolationControl, cancellationToken);

    private static async Task<KeyRingVerificationResult> VerifyCoreAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory,
        KeyRingIsolationControl? isolationControl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(providerFactory);

        if (KeyRingOperationScheduler.IsInsideCallback)
        {
            return Failure(
                scenario,
                KeyRingFailureKind.InvalidScenario,
                "Verification cannot be started from a provider, key-manager, rotation, or cleanup callback.");
        }

        var stopwatch = Stopwatch.StartNew();
        var canary = RandomNumberGenerator.GetBytes(32);
        ProviderScope providerScope;
        try
        {
            providerScope = await ProviderScope.CreateAsync(providerFactory.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(scenario, false, KeyRingFailureKind.Canceled, "Verification was canceled.", stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            return new(scenario, false, KeyRingFailureKind.Timeout, "Verification admission timed out before the configured bound.", stopwatch.Elapsed);
        }

        try
        {
            KeyRingVerificationResult result;

            if (scenario.IsRestartContinuity())
            {
                result = await VerifyContinuityAsync(scenario, providerFactory, canary, providerScope, cancellationToken).ConfigureAwait(false);
            }
            else if (scenario.IsReplicaSharing())
            {
                result = await VerifyReplicaSharingAsync(scenario, providerFactory, secondaryProviderFactory, canary, providerScope, cancellationToken).ConfigureAwait(false);
            }
            else if (scenario.IsApplicationIsolation())
            {
                result = await VerifyApplicationIsolationAsync(
                    scenario,
                    providerFactory,
                    secondaryProviderFactory,
                    isolationControl,
                    canary,
                    providerScope,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (scenario.IsPurposeIsolation())
            {
                result = await VerifyPurposeIsolationAsync(scenario, providerFactory, canary, providerScope, cancellationToken).ConfigureAwait(false);
            }
            else if (scenario.IsRotationContinuity())
            {
                result = await VerifyRotationContinuityAsync(scenario, providerFactory, canary, providerScope, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = Failure(scenario, KeyRingFailureKind.InvalidScenario, "The requested scenario is not supported.");
            }

            try
            {
                await providerScope.CompleteAsync(providerFactory.Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
            }

            stopwatch.Stop();
            return new KeyRingVerificationResult(scenario, result.Succeeded, result.FailureKind, result.Message, stopwatch.Elapsed);
        }
        finally
        {
            providerScope.Dispose();
        }
    }

    private static async Task<KeyRingVerificationResult> VerifyContinuityAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var first = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var protectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure;
        }

        var protectedResult = await ProtectAsync(
            scenario,
            protectorResult.Value!,
            canary,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectedResult.Failure is not null)
        {
            return protectedResult.Failure;
        }

        // Capture prior operation owners before retirement acquires its own lease.
        // Waiting on the scope's active count here would include that lease and deadlock.
        var quiescence = providerScope.CaptureQuiescence();
        var retirement = await ExecuteOwnedAsync(async _ =>
        {
            await quiescence.ConfigureAwait(false);
            return await providerScope.RetireAsync(first.Provider!).ConfigureAwait(false);
        }, providerFactory.Timeout, providerScope, cancellationToken).ConfigureAwait(false);
        protectorResult = default;
        if (retirement.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (retirement.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Restart provider retirement timed out before the configured bound.");
        }
        if (retirement.Exception is not null)
        {
            return Failure(scenario, KeyRingFailureKind.ProviderCreation, "Restart failed: the previous provider could not be retired before recreation.");
        }

        var second = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        if (ReferenceEquals(first.Provider, second.Provider))
        {
            return Failure(
                scenario,
                KeyRingFailureKind.ProviderCreation,
                "Provider creation failed: the restart factory reused the same provider instance.");
        }

        return await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            protectedResult.Value!,
            canary,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyReplicaSharingAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory,
        byte[] canary,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        if (secondaryProviderFactory is null)
        {
            return Failure(scenario, KeyRingFailureKind.InvalidScenario, "Replica sharing requires a second provider factory.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var second = await CreateProviderAsync(scenario, secondaryProviderFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        if (ReferenceEquals(first.Provider, second.Provider))
        {
            return Failure(
                scenario,
                KeyRingFailureKind.ProviderCreation,
                "Provider creation failed: replica factories returned the same provider instance.");
        }

        var firstProtectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (firstProtectorResult.Failure is not null)
        {
            return firstProtectorResult.Failure;
        }

        var secondProtectorResult = await CreateProtectorAsync(
            scenario,
            second.Provider!,
            SharedPurpose,
            secondaryProviderFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (secondProtectorResult.Failure is not null)
        {
            return secondProtectorResult.Failure;
        }

        var firstProtectedResult = await ProtectAsync(
            scenario,
            firstProtectorResult.Value!,
            canary,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (firstProtectedResult.Failure is not null)
        {
            return firstProtectedResult.Failure;
        }

        var secondProtectedResult = await ProtectAsync(
            scenario,
            secondProtectorResult.Value!,
            canary,
            secondaryProviderFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (secondProtectedResult.Failure is not null)
        {
            return secondProtectedResult.Failure;
        }

        var firstResult = await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            firstProtectedResult.Value!,
            canary,
            SharedPurpose,
            secondaryProviderFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (!firstResult.Succeeded)
        {
            return firstResult;
        }

        return await UnprotectAndCompareAsync(
            scenario,
            first.Provider!,
            secondProtectedResult.Value!,
            canary,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyPurposeIsolationAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var first = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var second = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        if (ReferenceEquals(first.Provider, second.Provider))
        {
            return Failure(
                scenario,
                KeyRingFailureKind.ProviderCreation,
                "Provider creation failed: purpose-isolation factory calls returned the same provider instance.");
        }

        var control = await VerifySharedBoundaryAsync(
            scenario,
            first.Provider!,
            second.Provider!,
            canary,
            providerFactory.Timeout,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (control is not null)
        {
            return control;
        }

        return await VerifyCrossBoundaryRejectionAsync(
            scenario,
            first.Provider!,
            second.Provider!,
            canary,
            SharedPurpose,
            AlternatePurpose,
            providerFactory.Timeout,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyApplicationIsolationAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory,
        KeyRingIsolationControl? isolationControl,
        byte[] canary,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        if (secondaryProviderFactory is null)
        {
            return Failure(scenario, KeyRingFailureKind.InvalidScenario, "Application isolation requires a second provider factory.");
        }

        if (isolationControl is null)
        {
            return Failure(
                scenario,
                KeyRingFailureKind.InvalidScenario,
                "Application isolation requires a same-boundary control with the same key store and application discriminator.");
        }

        if (!isolationControl.IsLinkedTo(providerFactory, secondaryProviderFactory))
        {
            return Failure(
                scenario,
                KeyRingFailureKind.InvalidScenario,
                "Application isolation requires all provider factories and the control to declare the same backing-store boundary.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var second = await CreateProviderAsync(scenario, secondaryProviderFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        if (ReferenceEquals(first.Provider, second.Provider))
        {
            return Failure(
                scenario,
                KeyRingFailureKind.ProviderCreation,
                "Provider creation failed: application-isolation factories returned the same provider instance.");
        }

        var controlFirst = await CreateProviderAsync(scenario, isolationControl.PrimaryFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (controlFirst.Failure is not null)
        {
            return controlFirst.Failure;
        }

        var controlSecond = await CreateProviderAsync(scenario, isolationControl.SecondaryFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (controlSecond.Failure is not null)
        {
            return controlSecond.Failure;
        }

        if (ReferenceEquals(controlFirst.Provider, controlSecond.Provider))
        {
            return Failure(
                scenario,
                KeyRingFailureKind.ProviderCreation,
                "Provider creation failed: application-isolation control factories returned the same provider instance.");
        }

        var control = await VerifySharedBoundaryAsync(
            scenario,
            controlFirst.Provider!,
            controlSecond.Provider!,
            canary,
            isolationControl.PrimaryFactory.Timeout,
            isolationControl.SecondaryFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (control is not null)
        {
            return control;
        }

        return await VerifyCrossBoundaryRejectionAsync(
            scenario,
            first.Provider!,
            second.Provider!,
            canary,
            SharedPurpose,
            SharedPurpose,
            providerFactory.Timeout,
            secondaryProviderFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult?> VerifySharedBoundaryAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider first,
        IDataProtectionProvider second,
        byte[] canary,
        TimeSpan firstTimeout,
        TimeSpan secondTimeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var firstProtected = await ProtectWithPurposeAsync(
            scenario,
            first,
            canary,
            SharedPurpose,
            firstTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (firstProtected.Failure is not null)
        {
            return firstProtected.Failure;
        }

        var secondResult = await UnprotectAndCompareAsync(
            scenario,
            second,
            firstProtected.Value!,
            canary,
            SharedPurpose,
            secondTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (!secondResult.Succeeded)
        {
            return secondResult.FailureKind is KeyRingFailureKind.Unprotect or KeyRingFailureKind.Protect
                ? Failure(
                    scenario,
                    KeyRingFailureKind.Unprotect,
                    "Isolation failed: the configured providers could not establish the shared-boundary control.")
                : secondResult;
        }

        var secondProtected = await ProtectWithPurposeAsync(
            scenario,
            second,
            canary,
            SharedPurpose,
            secondTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (secondProtected.Failure is not null)
        {
            return secondProtected.Failure;
        }

        var firstResult = await UnprotectAndCompareAsync(
            scenario,
            first,
            secondProtected.Value!,
            canary,
            SharedPurpose,
            firstTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (firstResult.Succeeded)
        {
            return null;
        }

        return firstResult.FailureKind is KeyRingFailureKind.Unprotect or KeyRingFailureKind.Protect
            ? Failure(
                scenario,
                KeyRingFailureKind.Unprotect,
                "Isolation failed: the configured providers could not establish the shared-boundary control.")
            : firstResult;
    }

    private static async Task<KeyRingVerificationResult> VerifyCrossBoundaryRejectionAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider first,
        IDataProtectionProvider second,
        byte[] canary,
        string firstPurpose,
        string secondPurpose,
        TimeSpan firstTimeout,
        TimeSpan secondTimeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var firstProtectorResult = await CreateProtectorAsync(
            scenario,
            first,
            firstPurpose,
            firstTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (firstProtectorResult.Failure is not null)
        {
            return firstProtectorResult.Failure;
        }

        var secondProtectorResult = await CreateProtectorAsync(
            scenario,
            second,
            secondPurpose,
            secondTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (secondProtectorResult.Failure is not null)
        {
            return secondProtectorResult.Failure;
        }

        var firstProtectedResult = await ProtectAsync(
            scenario,
            firstProtectorResult.Value!,
            canary,
            firstTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (firstProtectedResult.Failure is not null)
        {
            return firstProtectedResult.Failure;
        }

        var secondProtectedResult = await ProtectAsync(
            scenario,
            secondProtectorResult.Value!,
            canary,
            secondTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (secondProtectedResult.Failure is not null)
        {
            return secondProtectedResult.Failure;
        }

        var firstSelfResult = await UnprotectAndCompareAsync(
            scenario,
            first,
            firstProtectedResult.Value!,
            canary,
            firstPurpose,
            firstTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (!firstSelfResult.Succeeded)
        {
            return firstSelfResult.FailureKind is KeyRingFailureKind.Unprotect or KeyRingFailureKind.Protect
                ? Failure(
                    scenario,
                    KeyRingFailureKind.Unprotect,
                    "Isolation failed: the first provider could not complete its same-boundary control.")
                : firstSelfResult;
        }

        var secondSelfResult = await UnprotectAndCompareAsync(
            scenario,
            second,
            secondProtectedResult.Value!,
            canary,
            secondPurpose,
            secondTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (!secondSelfResult.Succeeded)
        {
            return secondSelfResult.FailureKind is KeyRingFailureKind.Unprotect or KeyRingFailureKind.Protect
                ? Failure(
                    scenario,
                    KeyRingFailureKind.Unprotect,
                    "Isolation failed: the second provider could not complete its same-boundary control.")
                : secondSelfResult;
        }

        var firstCrossResult = await ExpectUnprotectFailureAsync(
            scenario,
            second,
            firstProtectedResult.Value!,
            secondPurpose,
            secondTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (!firstCrossResult.Succeeded)
        {
            return firstCrossResult;
        }

        return await ExpectUnprotectFailureAsync(
            scenario,
            first,
            secondProtectedResult.Value!,
            firstPurpose,
            firstTimeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyRotationContinuityAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        if (!providerFactory.HasKeyManagerFactory || !providerFactory.HasNewKeyFactory)
        {
            return Failure(scenario, KeyRingFailureKind.RotationUnavailable, "Rotation continuity requires explicitly supplied key-manager and cancellation-aware key-creation factories.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var protectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure;
        }

        var protectedResult = await ProtectAsync(
            scenario,
            protectorResult.Value!,
            canary,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectedResult.Failure is not null)
        {
            return protectedResult.Failure;
        }

        var manager = await CreateKeyManagerAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (manager.Failure is not null)
        {
            return manager.Failure;
        }

        var rotationStart = DateTimeOffset.UtcNow;
        var activationDate = rotationStart.AddSeconds(-1);
        var expirationDate = rotationStart.AddDays(1);
        var rotationResult = await ExecuteOwnedAsync(
            token => KeyRingOperationScheduler.RunAsync(
                () => providerFactory.CreateNewKeyWithSnapshotAsync(
                    manager.Manager!, activationDate, expirationDate, SnapshotKeyIds, token), token),
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (rotationResult.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Rotation timed out before the configured bound.");
        }
        if (rotationResult.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (rotationResult.Exception is not null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager could not create a new key.");
        }

        if (rotationResult.Value.ExistingKeyIds is null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager could not expose its existing keys.");
        }

        if (rotationResult.Value.NewKey is null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager did not return a new key.");
        }

        var existingKeyIds = rotationResult.Value.ExistingKeyIds;
        var newKey = rotationResult.Value.NewKey;
        var observedKey = await ExecuteBoundedAsync(
            () => ObserveNewActiveKey(manager.Manager!, newKey, existingKeyIds, DateTimeOffset.UtcNow),
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (observedKey.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Rotation key observation timed out before the configured bound.");
        }
        if (observedKey.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (observedKey.Exception is not null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager could not expose the new key.");
        }

        if (!observedKey.Value.Value)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the new key was not observed as active.");
        }

        var second = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        if (ReferenceEquals(first.Provider, second.Provider))
        {
            return Failure(
                scenario,
                KeyRingFailureKind.ProviderCreation,
                "Provider creation failed: the rotation factory reused the same provider instance.");
        }

        var postRotationCanary = RandomNumberGenerator.GetBytes(32);
        var postRotationProtector = await CreateProtectorAsync(
            scenario,
            second.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (postRotationProtector.Failure is not null)
        {
            return postRotationProtector.Failure;
        }

        var postRotationProtected = await ProtectAsync(
            scenario,
            postRotationProtector.Value!,
            postRotationCanary,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (postRotationProtected.Failure is not null)
        {
            return postRotationProtected.Failure;
        }

        var payloadFormat = InspectPayloadFormat(postRotationProtected.Value!, observedKey.Value.KeyId);
        if (payloadFormat == RotationPayloadFormat.Unsupported)
        {
            return Failure(
                scenario,
                KeyRingFailureKind.RotationUnavailable,
                "Rotation unavailable: the recreated provider returned a payload outside the standard ASP.NET Core Data Protection payload format, so key adoption could not be verified.");
        }

        if (payloadFormat == RotationPayloadFormat.StandardButDifferentKey)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the recreated provider did not adopt the observed active key.");
        }

        var oldPayloadResult = await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            protectedResult.Value!,
            canary,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (!oldPayloadResult.Succeeded)
        {
            return oldPayloadResult;
        }

        return await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            postRotationProtected.Value!,
            postRotationCanary,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static HashSet<Guid> SnapshotKeyIds(IKeyManager manager)
    {
        var keyIds = new HashSet<Guid>();
        foreach (var key in manager.GetAllKeys())
        {
            if (key is not null)
            {
                keyIds.Add(key.KeyId);
            }
        }

        return keyIds;
    }

    private static RotationObservation ObserveNewActiveKey(
        IKeyManager manager,
        IKey newKey,
        HashSet<Guid> existingKeyIds,
        DateTimeOffset now)
    {
        var keyId = newKey.KeyId;
        if (existingKeyIds.Contains(keyId))
        {
            return new RotationObservation(keyId, false);
        }

        IKey? observedKey = null;
        foreach (var key in manager.GetAllKeys())
        {
            if (key is not null && key.KeyId == keyId)
            {
                if (observedKey is not null)
                {
                    return new RotationObservation(keyId, false);
                }

                observedKey = key;
            }
        }

        var active = observedKey is not null
            && !observedKey.IsRevoked
            && observedKey.ActivationDate <= now
            && now < observedKey.ExpirationDate;
        return new RotationObservation(keyId, active);
    }

    private static RotationPayloadFormat InspectPayloadFormat(byte[] protectedPayload, Guid keyId)
    {
        if (protectedPayload.Length <= 20
            || protectedPayload[0] != 0x09
            || protectedPayload[1] != 0xF0
            || protectedPayload[2] != 0xC9
            || protectedPayload[3] != 0xF0)
        {
            return RotationPayloadFormat.Unsupported;
        }

        var payloadKey = protectedPayload.AsSpan(4, 16);
        Span<byte> littleEndianKey = stackalloc byte[16];
        keyId.TryWriteBytes(littleEndianKey);
        // Canonical bytes can also match the alternate representation for symmetric IDs.
        if (payloadKey.SequenceEqual(littleEndianKey))
        {
            return RotationPayloadFormat.StandardWithExpectedKey;
        }

        // Every complete envelope names a canonical ID. Alternate-byte equality
        // can also name a distinct stale key and cannot establish producer intent.
        return RotationPayloadFormat.StandardButDifferentKey;
    }

    private enum RotationPayloadFormat
    {
        Unsupported,
        StandardButDifferentKey,
        StandardWithExpectedKey,
    }

    private readonly record struct RotationObservation(Guid KeyId, bool Value);

    private static async Task<(IDataProtector? Value, KeyRingVerificationResult? Failure)> CreateProtectorAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        string purpose,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteBoundedAsync(
            () => provider.CreateProtector(purpose),
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Protector creation timed out before the configured bound."));
        }
        if (operation.Canceled)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        if (operation.Exception is not null || operation.Value is null)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Protect, "Protect failed: the configured provider could not create a protector."));
        }

        return (operation.Value, null);
    }

    private static async Task<(byte[]? Value, KeyRingVerificationResult? Failure)> ProtectAsync(
        KeyRingScenario scenario,
        IDataProtector protector,
        byte[] canary,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteBoundedAsync(
            () => protector.Protect(canary.ToArray())?.ToArray(),
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Protect timed out before the configured bound."));
        }
        if (operation.Canceled)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        if (operation.Exception is not null || operation.Value is null)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Protect, "Protect failed: the configured provider could not protect the synthetic canary."));
        }

        return (operation.Value, null);
    }

    private static async Task<(byte[]? Value, KeyRingVerificationResult? Failure)> ProtectWithPurposeAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        byte[] canary,
        string purpose,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return (null, protectorResult.Failure);
        }

        return await ProtectAsync(
            scenario,
            protectorResult.Value!,
            canary,
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> UnprotectAndCompareAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        byte[] protectedPayload,
        byte[] canary,
        string purpose,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure.FailureKind == KeyRingFailureKind.Protect
                ? Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the configured provider could not create a protector.")
                : protectorResult.Failure;
        }

        var operation = await ExecuteBoundedAsync(
            () => protectorResult.Value!.Unprotect(protectedPayload.ToArray())?.ToArray(),
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Unprotect timed out before the configured bound.");
        }
        if (operation.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (operation.Exception is not null || operation.Value is null)
        {
            return Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: a payload that should survive the provider transition could not be read.");
        }

        return CryptographicOperations.FixedTimeEquals(operation.Value, canary)
            ? Success(scenario, "Continuity passed.")
            : Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the provider returned an unexpected payload.");
    }

    private static async Task<KeyRingVerificationResult> ExpectUnprotectFailureAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        byte[] protectedPayload,
        string purpose,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure.FailureKind == KeyRingFailureKind.Protect
                ? Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the configured provider could not create a protector.")
                : protectorResult.Failure;
        }

        var operation = await ExecuteBoundedAsync(
            () => protectorResult.Value!.Unprotect(protectedPayload.ToArray())?.ToArray(),
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Isolation check timed out before the configured bound.");
        }
        if (operation.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (operation.Exception is CryptographicException
            && protectorResult.Value is IPersistedDataProtector)
        {
            return Success(scenario, "Isolation passed.");
        }
        if (operation.Exception is not null)
        {
            return Failure(
                scenario,
                KeyRingFailureKind.Unprotect,
                "Isolation failed: the cross-boundary rejection was not attributable to the standard ASP.NET Core persisted-protector contract.");
        }

        return Failure(scenario, KeyRingFailureKind.UnexpectedCrossUnprotect, "Isolation failed: a provider from an isolated boundary successfully unprotected a payload.");
    }

    private static Task<BoundedOperation<T>> ExecuteBoundedAsync<T>(
        Func<T> operation,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken) =>
        ExecuteOwnedAsync(token => KeyRingOperationScheduler.Run(operation, token), timeout, providerScope, cancellationToken);

    private static async Task<BoundedOperation<T>> ExecuteOwnedAsync<T>(
        Func<CancellationToken, Task<T>> start,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken,
        Action<T>? acceptResult = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(default, null, false, true);
        }

        var lifetime = new OperationLifetime<T>(timeout, providerScope.EnterOperation(), start, acceptResult, cancellationToken);
        var task = lifetime.Operation;

        try
        {
            // Only the owned asynchronous signal wakes a stopped observer. A second
            // caller-token wait could run this continuation inline inside caller.Cancel.
            await Task.WhenAny(task, lifetime.Stopped).ConfigureAwait(false);

            var stop = lifetime.Seal();
            if (stop != StopCause.None)
            {
                return new(default, null, stop == StopCause.Deadline, stop == StopCause.Caller);
            }

            try
            {
                return new(await task.ConfigureAwait(false), null, false, false);
            }
            catch (Exception exception)
            {
                return new(default, exception, false, false);
            }
        }
        finally
        {
            ObserveFaults(lifetime.DisposeAsync().AsTask());
        }
    }

    private static async Task<T> AcceptAsync<T>(Task<T> task, Action<T>? acceptResult)
    {
        var value = await task.ConfigureAwait(false);
        acceptResult?.Invoke(value);
        return value;
    }

    private static void ObserveFaults(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // Owns the cancellation source through observer decision, actual operation completion,
    // input-relay quiescence, and the FIRST notification dispatch's completion.
    private sealed class OperationLifetime<T> : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _caller;
        private readonly IDisposable _lease;
        private readonly Timer _deadline;
        private readonly CancellationTokenRegistration _relay;
        private Task? _notification;
        private bool _sealed;
        private Task? _finalization;

        internal OperationLifetime(TimeSpan timeout, IDisposable lease, Func<CancellationToken, Task<T>> start, Action<T>? acceptResult, CancellationToken caller)
        {
            _caller = caller;
            _lease = lease;
            Token = _source.Token;
            // No finalizer is published until construction and operation setup finish.
            // An already-canceled caller can safely enter RequestStop during registration.
            _deadline = new Timer(static state => ((OperationLifetime<T>)state!).RequestStop(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _relay = caller.UnsafeRegister(static state => ((OperationLifetime<T>)state!).RequestStop(), this);
            _deadline.Change(timeout, Timeout.InfiniteTimeSpan);
            try { Operation = AcceptAsync(start(Token), acceptResult); }
            catch (Exception exception) { Operation = Task.FromException<T>(exception); }
        }

        internal Task<T> Operation { get; }
        private CancellationToken Token { get; }
        internal Task Stopped => _stopped.Task;

        internal void RequestStop()
        {
            lock (_gate)
            {
                if (_sealed || _notification is not null)
                {
                    return;
                }

                // CancelAsync sets token state synchronously but runs arbitrary registrations
                // asynchronously, outside the 32 callback slots. Later CancelAsync calls do
                // not join the first dispatch, so retain this task exactly once.
                _notification = _source.CancelAsync();
                _stopped.TrySetResult();
            }
        }

        internal StopCause Seal()
        {
            lock (_gate)
            {
                // Completion can win before the caller relay runs. Signal before selecting
                // a stop result, while the observer still owns the live source.
                var canceled = _caller.IsCancellationRequested;
                if (canceled) { RequestStop(); }
                _sealed = true;
                return _notification is null ? StopCause.None
                    : canceled ? StopCause.Caller : StopCause.Deadline;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _sealed = true;
                // Teardown is never awaited on the result path. Handles are fully initialized
                // and the observer cannot request another stop before finalization starts.
                return new(_finalization ??= FinalizeAsync());
            }
        }

        private async Task FinalizeAsync()
        {
            try
            {
                await _deadline.DisposeAsync().ConfigureAwait(false);
                await _relay.DisposeAsync().ConfigureAwait(false);
                try { await Operation.ConfigureAwait(false); }
                catch (Exception) { /* The observer or this owner consumes late operation faults. */ }
                if (_notification is not null)
                {
                    try { await _notification.ConfigureAwait(false); }
                    catch (Exception) { /* Notification failures never replace the stop verdict. */ }
                }
            }
            finally
            {
                _source.Dispose();
                _lease.Dispose();
            }
        }
    }

    private readonly record struct BoundedOperation<T>(T? Value, Exception? Exception, bool TimedOut, bool Canceled);

    private enum StopCause { None, Deadline, Caller }

    private static async Task<(IDataProtectionProvider? Provider, KeyRingVerificationResult? Failure)> CreateProviderAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteOwnedAsync(
            factory.CreateProviderAsync, factory.Timeout, providerScope, cancellationToken,
            provider => { if (provider is not null) { providerScope.Add(provider); } }).ConfigureAwait(false);
        if (operation.Canceled)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        if (operation.TimedOut)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Provider creation timed out before the configured bound."));
        }
        if (operation.Exception is not null)
        {
            return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory could not create a provider."));
        }
        return operation.Value is null
            ? (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory returned no provider."))
            : (operation.Value, null);
    }

    private static async Task<(IKeyManager? Manager, KeyRingVerificationResult? Failure)> CreateKeyManagerAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteOwnedAsync(factory.CreateKeyManagerAsync, factory.Timeout, providerScope, cancellationToken).ConfigureAwait(false);
        if (operation.Canceled)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        if (operation.TimedOut)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Key-manager creation timed out before the configured bound."));
        }
        if (operation.Exception is not null)
        {
            return (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory could not create a manager."));
        }
        return operation.Value is null
            ? (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory returned no manager."))
            : (operation.Value, null);
    }

    private static KeyRingVerificationResult Success(KeyRingScenario scenario, string message) =>
        new(scenario, true, KeyRingFailureKind.None, message, TimeSpan.Zero);

    private static KeyRingVerificationResult Failure(KeyRingScenario scenario, KeyRingFailureKind kind, string message) =>
        new(scenario, false, kind, message, TimeSpan.Zero);

    private sealed class ProviderScope : IDisposable
    {
        // Reserve ownership before creating providers, not after callbacks return. A blocked
        // operation or cleanup retains its reservation, bounding retained cleanup batches too.
        private static readonly SemaphoreSlim Lifecycles = new(32, 32);
        private readonly object _gate = new();
        private readonly HashSet<IDisposable> _providers = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IDataProtectionProvider, Task<bool>> _retirements = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<OperationLease> _operations = [];
        private readonly TaskCompletionSource _cleanupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeOperations;
        private bool _disposed;

        private ProviderScope() { }

        internal static async Task<ProviderScope> CreateAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!await Lifecycles.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException();
            }

            return new ProviderScope();
        }

        internal async Task CompleteAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Dispose();
            lock (_gate)
            {
                // A late operation still owns the provider. Do not add another timeout
                // to observation of an operation that already timed out or was canceled.
                if (_activeOperations != 0)
                {
                    return;
                }
            }

            try
            {
                await _cleanupCompletion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (TimeoutException)
            {
                // Cleanup is best effort and does not replace the scenario verdict.
                // The reservation remains held until cleanup actually finishes.
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        internal IDisposable EnterOperation()
        {
            lock (_gate)
            {
                _activeOperations++;
                var lease = new OperationLease(this);
                _operations.Add(lease);
                return lease;
            }
        }

        internal Task CaptureQuiescence()
        {
            lock (_gate) { return Task.WhenAll(_operations.Select(operation => operation.Completion)); }
        }

        internal Task<bool> RetireAsync(IDataProtectionProvider provider)
        {
            lock (_gate)
            {
                if (_retirements.TryGetValue(provider, out var existing)) { return existing; }
                if (provider is not IDisposable disposable)
                {
                    return _retirements[provider] = Task.FromResult(true);
                }

                _providers.Remove(disposable);
                // Once transferred, teardown cannot be canceled or retried by final cleanup.
                return _retirements[provider] = KeyRingOperationScheduler.Run(() =>
                {
                    disposable.Dispose();
                    return true;
                }, CancellationToken.None);
            }
        }

        internal void Add(IDataProtectionProvider provider)
        {
            if (provider is not IDisposable disposable)
            {
                return;
            }

            lock (_gate)
            {
                // Add is always called under an active operation lease, including
                // a factory that returns after the caller has stopped waiting.
                if (!_retirements.ContainsKey(provider)) { _providers.Add(disposable); }
            }
        }

        public void Dispose()
        {
            IDisposable[]? providers = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_activeOperations == 0)
                {
                    providers = TakeProviders();
                }
            }

            StartCleanup(providers);
        }

        private void ExitOperation(OperationLease operation)
        {
            IDisposable[]? providers = null;
            lock (_gate)
            {
                _activeOperations--;
                _operations.Remove(operation);
                if (_disposed && _activeOperations == 0)
                {
                    providers = TakeProviders();
                }
            }

            StartCleanup(providers);
        }

        private IDisposable[] TakeProviders()
        {
            var providers = _providers.ToArray();
            _providers.Clear();
            return providers;
        }

        private void StartCleanup(IDisposable[]? providers)
        {
            if (providers is null)
            {
                return;
            }

            _ = CleanupAsync(providers);
        }

        private async Task CleanupAsync(IDisposable[] providers)
        {
            try
            {
                if (providers.Length != 0)
                {
                    // One batch per reserved lifecycle. Use the same callback slots
                    // and re-entry guard as provider operations, without canceling
                    // cleanup of providers whose ownership we already accepted.
                    await KeyRingOperationScheduler.Run(() =>
                    {
                        for (var index = providers.Length - 1; index >= 0; index--)
                        {
                            DisposeProvider(providers[index]);
                        }

                        return true;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                Lifecycles.Release();
                _cleanupCompletion.TrySetResult();
            }
        }

        private static void DisposeProvider(IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Disposal is best effort; provider operation failures remain the verification result.
            }
        }

        private sealed class OperationLease : IDisposable
        {
            private ProviderScope? _owner;
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Task Completion => _completion.Task;

            internal OperationLease(ProviderScope owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null) { return; }
                owner.ExitOperation(this);
                _completion.TrySetResult();
            }
        }
    }

}

internal static class KeyRingScenarioExtensions
{
    internal static bool IsRestartContinuity(this KeyRingScenario scenario) => scenario == KeyRingScenario.RestartContinuity;

    internal static bool IsReplicaSharing(this KeyRingScenario scenario) => scenario == KeyRingScenario.ReplicaSharing;

    internal static bool IsApplicationIsolation(this KeyRingScenario scenario) => scenario == KeyRingScenario.ApplicationIsolation;

    internal static bool IsPurposeIsolation(this KeyRingScenario scenario) => scenario == KeyRingScenario.PurposeIsolation;

    internal static bool IsRotationContinuity(this KeyRingScenario scenario) => scenario == KeyRingScenario.RotationContinuity;
}
