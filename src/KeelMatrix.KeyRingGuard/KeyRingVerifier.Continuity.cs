using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard;

public static partial class KeyRingVerifier
{
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
}
