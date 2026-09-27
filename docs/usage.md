# KeyRingGuard usage

## Install

Install both packages into the test project that owns the provider configuration:

```text
dotnet add package KeelMatrix.KeyRingGuard
dotnet add package Microsoft.AspNetCore.DataProtection.Extensions
```

The Extensions package provides `DataProtectionProvider.Create`, while KeyRingGuard keeps provider integrations consumer-owned.

## Quick Start

The package does not configure Data Protection for the application. Pass a factory that creates the same provider configuration the application uses, normally with a temporary or dedicated test store.

```csharp
using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;

var keyStorePath = Path.Combine(Path.GetTempPath(), "keyringguard-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(keyStorePath);

try
{
    var factory = new KeyRingProviderFactory(
        _ => DataProtectionProvider.Create(
            new DirectoryInfo(keyStorePath),
            builder => builder.SetApplicationName("Sample.App")),
        TimeSpan.FromSeconds(5));

    var result = await KeyRingVerifier.VerifyAsync(
        KeyRingScenario.RestartContinuity,
        factory);

    if (!result.Succeeded)
    {
        throw new InvalidOperationException(result.Message);
    }

    Console.WriteLine("KeyRingGuard quick start: PASS");
}
finally
{
    Directory.Delete(keyStorePath, recursive: true);
}
```

Factories are caller-owned. KeyRingGuard does not silently replace a failed or missing factory with an in-memory provider.

> Warning: Data Protection can auto-generate keys while a provider is initialized. A check against a real shared store may mutate that store even when the scenario itself only verifies continuity or isolation. `KeyManagementOptions.AutoGenerateKeys` controls this behavior for the caller's provider configuration. Use a temporary or dedicated test store by default.

## Restart continuity

`RestartContinuity` creates a provider, protects a generated in-memory canary, creates an independent second provider from the same factory, and checks that the second provider can unprotect the payload. Providers are disposed after the scenario has no outstanding callbacks; this keeps a timed-out callback from using a provider after disposal. A factory that returns the same live provider instance is rejected. This is the test for persistence across a process restart or deployment replacement.

## Replica sharing

`ReplicaSharing` accepts a second factory and checks both directions. Configure both factories with the intended shared store and the same application discriminator:

```csharp
var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.ReplicaSharing,
    replicaA,
    replicaB);
```

The scenario passes only when A can read B's payload and B can read A's payload.

## Application-name isolation

`ApplicationIsolation` accepts two factories configured with different application discriminators plus a same-boundary control. Because provider factories are opaque, declare one `KeyRingBoundary` instance on every factory that represents the same backing store. The control factories must use that same store and application discriminator and must exchange payloads in both directions before the isolated cross-provider checks run:

```csharp
var boundary = new KeyRingBoundary();
// Configure applicationA, applicationB, sharedApplicationA, and sharedApplicationB with boundary.
var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
    KeyRingScenario.ApplicationIsolation,
    applicationA,
    applicationB,
    new KeyRingIsolationControl(
        sharedApplicationA,
        sharedApplicationB),
    CancellationToken.None);
```

Each provider must first protect and unprotect its own payload, and an unexpected successful cross-unprotect is reported as `UnexpectedCrossUnprotect` and fails the result. A missing or mismatched boundary is an invalid scenario, so an unrelated third-store control cannot turn a storage mismatch into isolation evidence. A cryptographic rejection is accepted only after those same-boundary controls succeed; unrelated provider failures remain failures.

## Purpose isolation

`PurposeIsolation` creates two independent providers from the supplied factory, first proves that they can exchange a same-purpose payload, then protects with two different purposes and expects cross-purpose unprotect to fail. The purpose strings are owned by the verifier so the test remains synthetic and does not require a caller payload. A factory that silently selects a different store on each call fails the shared-boundary control.

## Rotation continuity

`RotationContinuity` is opt-in because it changes the supplied key store by asking the supplied `IKeyManager` to create one new key. Before invoking the callback, the verifier snapshots the manager's key IDs; the callback must return a key ID that was absent from that snapshot. It then proves exactly one matching observed key is present, non-revoked, active, and adopted by a recreated independent provider, and checks both the old and new payloads. Resolve the provider and manager from the same provider setup for each factory call, and use one boundary identity to record that caller-declared linkage:

```csharp
var boundary = new KeyRingBoundary();
var factory = new KeyRingProviderFactory(
    _ => DataProtectionProvider.Create(new DirectoryInfo(keyStorePath), builder => builder.SetApplicationName("Orders.App")),
    TimeSpan.FromSeconds(5),
    _ => keyManager,
    (manager, activationDate, expirationDate, cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return manager.CreateNewKey(activationDate, expirationDate);
        }, cancellationToken),
    boundary);

var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.RotationContinuity,
    factory);
```

If no linked key-manager and key-creation factories are supplied, the result is `RotationUnavailable`. KeyRingGuard never deletes or revokes keys. A rotation callback that returns a pre-existing key is rejected as a failed rotation, even when that key is active. A callback that ignores cancellation may continue after a timeout; callbacks must check cancellation before mutating the store and honor it during their work.

## Provider-factory patterns

The factory may be synchronous or asynchronous. The verifier passes a cancellation token to provider, key-manager, and rotation callbacks and applies the configured per-operation timeout to provider creation, protector creation, protect, unprotect, key observation, and rotation. Queue admission canceled by that internal timeout is reported as `Timeout`, while caller cancellation is reported as `Canceled`; queued callbacks that miss admission are not invoked. Synchronous callbacks run through a bounded shared scheduler with dedicated execution slots, so repeated hung callbacks cannot starve timeout observation or create an unbounded set of dedicated threads. .NET cannot forcibly interrupt a synchronous callback that is already running; it may finish in the background. Providers remain alive until scheduled callbacks finish, including callbacks that outlive a timeout, and late provider results are disposed when they arrive. Cancellation before scheduler admission removes queued work without invoking its callback; cancellation is otherwise cooperative for asynchronous callbacks. Use the asynchronous constructor for network-backed provider creation and honor the token in every provider, manager, and rotation callback.

Provider exceptions are categorized without copying exception text into the result. Results distinguish provider creation, protect, unprotect, unexpected cross-unprotect, timeout, cancellation, and rotation failures.

## Filesystem test stores

Create a unique directory below the operating system temporary directory for each test, configure the provider to persist there, and delete it in a `finally`/`Dispose` path. Keep the store dedicated to the test. Do not point a continuity test at a developer profile or a shared production key ring.

Data Protection can auto-generate keys. A verification against a real shared store may therefore mutate that store even when the test only reads a payload. `KeyManagementOptions.AutoGenerateKeys` controls this behavior for the caller's provider configuration; use a temporary or dedicated store by default and make any deliberate shared-store mutation explicit in the test setup.

## Network-backed stores

KeyRingGuard itself performs no network calls and ships no Redis, Azure, AWS, or database SDK. A caller may supply a factory backed by one of those systems. Bound every provider operation with a finite timeout, honor cancellation, use test credentials through the host's secure configuration, and isolate the test data. A timeout or provider failure is returned as a bounded result; it is never replaced with an in-memory provider.

## Safety and privacy

The canary is random, bounded, ephemeral, and kept in memory. Result messages never include canaries, protected payloads, key XML, master material, key identifiers, store paths, application names, or raw provider exception text. KeyRingGuard has no telemetry dependency.
