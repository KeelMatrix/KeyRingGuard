using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard;

public static partial class KeyRingVerifier
{
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
}
