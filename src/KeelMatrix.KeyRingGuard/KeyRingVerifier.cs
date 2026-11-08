using System.Diagnostics;
using System.Security.Cryptography;

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
public static partial class KeyRingVerifier
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

    private static KeyRingVerificationResult Success(KeyRingScenario scenario, string message) =>
        new(scenario, true, KeyRingFailureKind.None, message, TimeSpan.Zero);

    private static KeyRingVerificationResult Failure(KeyRingScenario scenario, KeyRingFailureKind kind, string message) =>
        new(scenario, false, kind, message, TimeSpan.Zero);
}
