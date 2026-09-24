# KeyRingGuard usage

## Install

Install the package into the test project that owns the provider configuration:

```text
dotnet add package KeelMatrix.KeyRingGuard
```

## Quick Start

The package does not configure Data Protection for the application. Pass a factory that creates the same provider configuration the application uses, normally with a temporary or dedicated test store.

```csharp
var factory = new KeyRingProviderFactory(
    _ => DataProtectionProvider.Create(
        keyStorePath,
        builder => builder.SetApplicationName("Orders.App")),
    TimeSpan.FromSeconds(5));

var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.RestartContinuity,
    factory);

Assert.True(result.Succeeded, result.Message);
```

Factories are caller-owned. KeyRingGuard does not silently replace a failed or missing factory with an in-memory provider.

## Restart continuity

`RestartContinuity` creates a provider, protects a generated in-memory canary, creates a second provider from the same factory, and checks that the second provider can unprotect the payload. This is the test for persistence across a process restart or deployment replacement.

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

`ApplicationIsolation` accepts two factories configured with different application discriminators. It protects with each provider and expects both cross-provider unprotect operations to fail:

```csharp
var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.ApplicationIsolation,
    applicationA,
    applicationB);
```

An unexpected successful cross-unprotect is reported as `UnexpectedCrossUnprotect` and fails the result.
Any `CryptographicException`, including a derived exception type, is treated as the expected isolation rejection.

## Purpose isolation

`PurposeIsolation` creates two providers from the supplied factory, protects with two different purposes, and expects cross-purpose unprotect to fail. The purpose strings are owned by the verifier so the test remains synthetic and does not require a caller payload.

## Rotation continuity

`RotationContinuity` is opt-in because it changes the supplied key store by asking the supplied `IKeyManager` to create one new key. Provide a key-manager factory that uses the same dedicated store as the provider factory:

```csharp
var factory = new KeyRingProviderFactory(
    _ => DataProtectionProvider.Create(keyStorePath, builder => builder.SetApplicationName("Orders.App")),
    TimeSpan.FromSeconds(5),
    _ => keyManager);

var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.RotationContinuity,
    factory);
```

If no key-manager factory is supplied, the result is `RotationUnavailable`. KeyRingGuard never deletes or revokes keys.

## Provider-factory patterns

The factory may be synchronous or asynchronous. The verifier passes a cancellation token to provider and key-manager factories and applies the configured per-operation timeout to provider creation, protector creation, protect, unprotect, and rotation. Synchronous callbacks run on a worker so an uncooperative callback cannot hold the verifier past its bound, but .NET cannot forcibly interrupt a synchronous callback that is already running; it may finish in the background. Cancellation is cooperative for asynchronous callbacks. Use the asynchronous constructor for network-backed provider creation and honor the token in the provider setup path.

Provider exceptions are categorized without copying exception text into the result. Results distinguish provider creation, protect, unprotect, unexpected cross-unprotect, timeout, cancellation, and rotation failures.

## Filesystem test stores

Create a unique directory below the operating system temporary directory for each test, configure the provider to persist there, and delete it in a `finally`/`Dispose` path. Keep the store dedicated to the test. Do not point a continuity test at a developer profile or a shared production key ring.

Data Protection can auto-generate keys. A verification against a real shared store may therefore mutate that store even when the test only reads a payload. `KeyManagementOptions.AutoGenerateKeys` controls this behavior for the caller's provider configuration; use a temporary or dedicated store by default and make any deliberate shared-store mutation explicit in the test setup.

## Network-backed stores

KeyRingGuard itself performs no network calls and ships no Redis, Azure, AWS, or database SDK. A caller may supply a factory backed by one of those systems. Bound every provider operation with a finite timeout, honor cancellation, use test credentials through the host's secure configuration, and isolate the test data. A timeout or provider failure is returned as a bounded result; it is never replaced with an in-memory provider.

## Safety and privacy

The canary is random, bounded, ephemeral, and kept in memory. Result messages never include canaries, protected payloads, key XML, master material, key identifiers, store paths, application names, or raw provider exception text. KeyRingGuard has no telemetry dependency.
