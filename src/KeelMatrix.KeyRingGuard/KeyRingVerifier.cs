using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Executes provider-neutral Data Protection continuity and isolation scenarios.
/// </summary>
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
    /// <param name="cancellationToken">Cancels provider creation and verification operations.</param>
    /// <returns>A structured result that never includes key material, canaries, or raw provider exceptions.</returns>
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
    /// on both sides so that rejection is not attributed to an unrelated provider mismatch.
    /// </summary>
    /// <param name="scenario">The immutable scenario description to execute.</param>
    /// <param name="providerFactory">The primary provider factory.</param>
    /// <param name="secondaryProviderFactory">The second factory required by replica and isolation scenarios.</param>
    /// <param name="isolationControl">The same-boundary control required by application isolation.</param>
    /// <param name="cancellationToken">Cancels provider creation and verification operations.</param>
    /// <returns>A structured result that never includes key material, canaries, or raw provider exceptions.</returns>
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

        var stopwatch = Stopwatch.StartNew();
        var canary = RandomNumberGenerator.GetBytes(32);
        KeyRingVerificationResult result;

        if (scenario.IsRestartContinuity())
        {
            result = await VerifyContinuityAsync(scenario, providerFactory, canary, cancellationToken).ConfigureAwait(false);
        }
        else if (scenario.IsReplicaSharing())
        {
            result = await VerifyReplicaSharingAsync(scenario, providerFactory, secondaryProviderFactory, canary, cancellationToken).ConfigureAwait(false);
        }
        else if (scenario.IsApplicationIsolation())
        {
            result = await VerifyApplicationIsolationAsync(
                scenario,
                providerFactory,
                secondaryProviderFactory,
                isolationControl,
                canary,
                cancellationToken).ConfigureAwait(false);
        }
        else if (scenario.IsPurposeIsolation())
        {
            result = await VerifyPurposeIsolationAsync(scenario, providerFactory, canary, cancellationToken).ConfigureAwait(false);
        }
        else if (scenario.IsRotationContinuity())
        {
            result = await VerifyRotationContinuityAsync(scenario, providerFactory, canary, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = Failure(scenario, KeyRingFailureKind.InvalidScenario, "The requested scenario is not supported.");
        }

        stopwatch.Stop();
        return new KeyRingVerificationResult(scenario, result.Succeeded, result.FailureKind, result.Message, stopwatch.Elapsed);
    }

    private static async Task<KeyRingVerificationResult> VerifyContinuityAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        CancellationToken cancellationToken)
    {
        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var protectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
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
            cancellationToken).ConfigureAwait(false);
        if (protectedResult.Failure is not null)
        {
            return protectedResult.Failure;
        }

        DisposeProvider(first.Provider!);
        var second = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyReplicaSharingAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory,
        byte[] canary,
        CancellationToken cancellationToken)
    {
        if (secondaryProviderFactory is null)
        {
            return Failure(scenario, KeyRingFailureKind.InvalidScenario, "Replica sharing requires a second provider factory.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var second = await CreateProviderAsync(scenario, secondaryProviderFactory, cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyPurposeIsolationAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        CancellationToken cancellationToken)
    {
        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var second = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyApplicationIsolationAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory,
        KeyRingIsolationControl? isolationControl,
        byte[] canary,
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

        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var second = await CreateProviderAsync(scenario, secondaryProviderFactory, cancellationToken).ConfigureAwait(false);
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

        var controlFirst = await CreateProviderAsync(scenario, isolationControl.PrimaryFactory, cancellationToken).ConfigureAwait(false);
        if (controlFirst.Failure is not null)
        {
            return controlFirst.Failure;
        }

        var controlSecond = await CreateProviderAsync(scenario, isolationControl.SecondaryFactory, cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult?> VerifySharedBoundaryAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider first,
        IDataProtectionProvider second,
        byte[] canary,
        TimeSpan firstTimeout,
        TimeSpan secondTimeout,
        CancellationToken cancellationToken)
    {
        var firstProtected = await ProtectWithPurposeAsync(
            scenario,
            first,
            canary,
            SharedPurpose,
            firstTimeout,
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
        CancellationToken cancellationToken)
    {
        var firstProtectorResult = await CreateProtectorAsync(
            scenario,
            first,
            firstPurpose,
            firstTimeout,
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyRotationContinuityAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        CancellationToken cancellationToken)
    {
        if (!providerFactory.HasKeyManagerFactory)
        {
            return Failure(scenario, KeyRingFailureKind.RotationUnavailable, "Rotation continuity requires an explicitly supplied key-manager factory.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var protectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
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
            cancellationToken).ConfigureAwait(false);
        if (protectedResult.Failure is not null)
        {
            return protectedResult.Failure;
        }

        var manager = await CreateKeyManagerAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (manager.Failure is not null)
        {
            return manager.Failure;
        }

        var rotationResult = await ExecuteBoundedAsync(
            () => manager.Manager!.CreateNewKey(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(1)),
            providerFactory.Timeout,
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

        if (rotationResult.Value is null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager did not return a new key.");
        }

        var newKey = rotationResult.Value;
        var observedKeys = await ExecuteBoundedAsync(
            () => manager.Manager!.GetAllKeys(),
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (observedKeys.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Rotation key observation timed out before the configured bound.");
        }
        if (observedKeys.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (observedKeys.Exception is not null || observedKeys.Value is null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager could not expose the new key.");
        }

        var now = DateTimeOffset.UtcNow;
        var keyIsObservedAndActive = observedKeys.Value.Any(key => key.KeyId == newKey.KeyId)
            && newKey.ActivationDate <= now
            && now < newKey.ExpirationDate;
        if (!keyIsObservedAndActive)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the new key was not observed as active.");
        }

        DisposeProvider(first.Provider!);
        var second = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
        if (postRotationProtected.Failure is not null)
        {
            return postRotationProtected.Failure;
        }

        var oldPayloadResult = await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            protectedResult.Value!,
            canary,
            SharedPurpose,
            providerFactory.Timeout,
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(IDataProtector? Value, KeyRingVerificationResult? Failure)> CreateProtectorAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        string purpose,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteBoundedAsync(
            () => provider.CreateProtector(purpose),
            timeout,
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
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteBoundedAsync(
            () => protector.Protect(canary),
            timeout,
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
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> UnprotectAndCompareAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        byte[] protectedPayload,
        byte[] canary,
        string purpose,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure.FailureKind == KeyRingFailureKind.Protect
                ? Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the configured provider could not create a protector.")
                : protectorResult.Failure;
        }

        var operation = await ExecuteBoundedAsync(
            () => protectorResult.Value!.Unprotect(protectedPayload),
            timeout,
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
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure.FailureKind == KeyRingFailureKind.Protect
                ? Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the configured provider could not create a protector.")
                : protectorResult.Failure;
        }

        var operation = await ExecuteBoundedAsync(
            () => protectorResult.Value!.Unprotect(protectedPayload),
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Isolation check timed out before the configured bound.");
        }
        if (operation.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (operation.Exception is CryptographicException)
        {
            return Success(scenario, "Isolation passed.");
        }
        if (operation.Exception is not null)
        {
            return Failure(scenario, KeyRingFailureKind.Unprotect, "Isolation failed: the configured provider could not complete the cross-boundary check.");
        }

        return Failure(scenario, KeyRingFailureKind.UnexpectedCrossUnprotect, "Isolation failed: a provider from an isolated boundary successfully unprotected a payload.");
    }

    private static async Task<BoundedOperation<T>> ExecuteBoundedAsync<T>(
        Func<T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<T> task;
        try
        {
            task = Task.Factory.StartNew(
                operation,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            ObserveFaults(task);
        }
        catch (Exception exception)
        {
            return new(default, exception, false, false);
        }

        try
        {
            return new(await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false), null, false, false);
        }
        catch (TimeoutException)
        {
            return new(default, null, true, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(default, null, false, true);
        }
        catch (Exception exception)
        {
            return new(default, exception, false, false);
        }
    }

    private static void ObserveFaults<T>(Task<T> task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private readonly record struct BoundedOperation<T>(T? Value, Exception? Exception, bool TimedOut, bool Canceled);

    private static async Task<(IDataProtectionProvider? Provider, KeyRingVerificationResult? Failure)> CreateProviderAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(factory.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<IDataProtectionProvider> task;

        try
        {
            task = factory.CreateProviderAsync(linkedSource.Token);
            if (task is null)
            {
                return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the factory returned no operation."));
            }
            ObserveFaults(task);
        }
        catch (Exception)
        {
            return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory could not create a provider."));
        }

        try
        {
            var provider = await task.WaitAsync(factory.Timeout, cancellationToken).ConfigureAwait(false);
            return provider is null
                ? (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory returned no provider."))
                : (provider, null);
        }
        catch (TimeoutException)
        {
            linkedSource.Cancel();
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Provider creation timed out before the configured bound."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        catch (OperationCanceledException)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Provider creation timed out before the configured bound."));
        }
        catch (Exception)
        {
            return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory could not create a provider."));
        }
    }

    private static async Task<(IKeyManager? Manager, KeyRingVerificationResult? Failure)> CreateKeyManagerAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(factory.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<IKeyManager> task;

        try
        {
            task = factory.CreateKeyManagerAsync(linkedSource.Token);
            ObserveFaults(task);
        }
        catch (Exception)
        {
            return (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory could not create a manager."));
        }

        try
        {
            var manager = await task.WaitAsync(factory.Timeout, cancellationToken).ConfigureAwait(false);
            return manager is null
                ? (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory returned no manager."))
                : (manager, null);
        }
        catch (TimeoutException)
        {
            linkedSource.Cancel();
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Key-manager creation timed out before the configured bound."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        catch (OperationCanceledException)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Key-manager creation timed out before the configured bound."));
        }
        catch (Exception)
        {
            return (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory could not create a manager."));
        }
    }

    private static KeyRingVerificationResult Success(KeyRingScenario scenario, string message) =>
        new(scenario, true, KeyRingFailureKind.None, message, TimeSpan.Zero);

    private static KeyRingVerificationResult Failure(KeyRingScenario scenario, KeyRingFailureKind kind, string message) =>
        new(scenario, false, kind, message, TimeSpan.Zero);

    private static void DisposeProvider(IDataProtectionProvider provider)
    {
        if (provider is IDisposable disposable)
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
