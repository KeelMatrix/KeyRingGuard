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
        using var providerScope = new ProviderScope();
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
        using var rotationTimeoutSource = new CancellationTokenSource(providerFactory.Timeout);
        using var rotationLinkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            rotationTimeoutSource.Token);
        var rotationResult = await ExecuteAsync(
            () => providerFactory.CreateNewKeyWithSnapshotAsync(
                manager.Manager!,
                activationDate,
                expirationDate,
                SnapshotKeyIds,
                rotationLinkedSource.Token),
            providerFactory.Timeout,
            providerScope,
            rotationLinkedSource.Token,
            cancellationToken).ConfigureAwait(false);
        if (rotationResult.TimedOut)
        {
            rotationLinkedSource.Cancel();
            return Failure(scenario, KeyRingFailureKind.Timeout, "Rotation timed out before the configured bound.");
        }
        if (rotationResult.Canceled)
        {
            rotationLinkedSource.Cancel();
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

        if (!PayloadUsesKey(postRotationProtected.Value!, observedKey.Value.KeyId))
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

    private static bool PayloadUsesKey(byte[] protectedPayload, Guid keyId)
    {
        if (protectedPayload.Length < 20
            || protectedPayload[0] != 0x09
            || protectedPayload[1] != 0xF0
            || protectedPayload[2] != 0xC9
            || protectedPayload[3] != 0xF0)
        {
            return false;
        }

        var payloadKey = protectedPayload.AsSpan(4, 16);
        Span<byte> littleEndianKey = stackalloc byte[16];
        Span<byte> bigEndianKey = stackalloc byte[16];
        keyId.TryWriteBytes(littleEndianKey);
        keyId.TryWriteBytes(bigEndianKey, bigEndian: true, out _);
        return payloadKey.SequenceEqual(littleEndianKey) || payloadKey.SequenceEqual(bigEndianKey);
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
            () => protector.Protect(canary),
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
            () => protectorResult.Value!.Unprotect(protectedPayload),
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
            () => protectorResult.Value!.Unprotect(protectedPayload),
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
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(default, null, false, true);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<T> task;
        try
        {
            task = RunScoped(operation, providerScope, linkedSource.Token);
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
            linkedSource.Cancel();
            return new(default, null, true, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(default, null, false, true);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return new(default, null, true, false);
        }
        catch (Exception exception)
        {
            return new(default, exception, false, false);
        }
    }

    private static async Task<BoundedOperation<T>> ExecuteAsync<T>(
        Func<Task<T>> operation,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken startCancellationToken,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(default, null, false, true);
        }

        Task<T> task;
        try
        {
            task = RunScopedAsync(operation, providerScope, startCancellationToken);
            if (task is null)
            {
                return new(default, new InvalidOperationException(), false, false);
            }

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
        catch (OperationCanceledException) when (startCancellationToken.IsCancellationRequested)
        {
            return new(default, null, true, false);
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

    private static Task<T> RunScoped<T>(
        Func<T> operation,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var lease = providerScope.EnterOperation();
        try
        {
            return ReleaseAfterCompletion(
                KeyRingOperationScheduler.Run(operation, cancellationToken),
                lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static Task<T> RunScopedAsync<T>(
        Func<Task<T>> operation,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var lease = providerScope.EnterOperation();
        try
        {
            return ReleaseAfterCompletion(
                KeyRingOperationScheduler.RunAsync(operation, cancellationToken),
                lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static async Task<T> ReleaseAfterCompletion<T>(Task<T> task, IDisposable lease)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static async Task<IDataProtectionProvider> TrackProviderAsync(
        Task<IDataProtectionProvider> task,
        ProviderScope providerScope,
        IDisposable lease)
    {
        try
        {
            var provider = await task.ConfigureAwait(false);
            if (provider is not null)
            {
                providerScope.Add(provider);
            }

            return provider!;
        }
        finally
        {
            lease.Dispose();
        }
    }

    private readonly record struct BoundedOperation<T>(T? Value, Exception? Exception, bool TimedOut, bool Canceled);

    private static async Task<(IDataProtectionProvider? Provider, KeyRingVerificationResult? Failure)> CreateProviderAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }

        using var timeoutSource = new CancellationTokenSource(factory.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<IDataProtectionProvider> task;
        IDisposable? lease = null;

        try
        {
            lease = providerScope.EnterOperation();
            var factoryTask = factory.CreateProviderAsync(linkedSource.Token);
            if (factoryTask is null)
            {
                lease.Dispose();
                lease = null;
                return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the factory returned no operation."));
            }

            task = TrackProviderAsync(factoryTask, providerScope, lease);
            lease = null;
            ObserveFaults(task);
        }
        catch (Exception)
        {
            lease?.Dispose();
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
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }

        using var timeoutSource = new CancellationTokenSource(factory.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<IKeyManager> task;
        IDisposable? lease = null;

        try
        {
            lease = providerScope.EnterOperation();
            var factoryTask = factory.CreateKeyManagerAsync(linkedSource.Token);
            if (factoryTask is null)
            {
                lease.Dispose();
                lease = null;
                return (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory returned no operation."));
            }

            task = ReleaseAfterCompletion(factoryTask, lease);
            lease = null;
            ObserveFaults(task);
        }
        catch (Exception)
        {
            lease?.Dispose();
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

    private sealed class ProviderScope : IDisposable
    {
        private readonly object _gate = new();
        private readonly HashSet<IDisposable> _providers = new(ReferenceEqualityComparer.Instance);
        private int _activeOperations;
        private bool _disposed;

        internal IDisposable EnterOperation()
        {
            lock (_gate)
            {
                _activeOperations++;
            }

            return new OperationLease(this);
        }

        internal void Add(IDataProtectionProvider provider)
        {
            if (provider is not IDisposable disposable)
            {
                return;
            }

            var disposeImmediately = false;
            lock (_gate)
            {
                if (_disposed && _activeOperations == 0)
                {
                    disposeImmediately = true;
                }
                else
                {
                    _providers.Add(disposable);
                }
            }

            if (disposeImmediately)
            {
                DisposeProvider(disposable);
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

            DisposeProviders(providers);
        }

        private void ExitOperation()
        {
            IDisposable[]? providers = null;
            lock (_gate)
            {
                _activeOperations--;
                if (_disposed && _activeOperations == 0)
                {
                    providers = TakeProviders();
                }
            }

            DisposeProviders(providers);
        }

        private IDisposable[] TakeProviders()
        {
            var providers = _providers.ToArray();
            _providers.Clear();
            return providers;
        }

        private static void DisposeProviders(IDisposable[]? providers)
        {
            if (providers is null)
            {
                return;
            }

            for (var index = providers.Length - 1; index >= 0; index--)
            {
                DisposeProvider(providers[index]);
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

            internal OperationLease(ProviderScope owner) => _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitOperation();
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
