# KeelMatrix.KeyRingGuard

`KeelMatrix.KeyRingGuard` verifies the Data Protection transitions an application depends on: restart continuity, replica sharing, intentional application and purpose isolation, and deterministic key-rotation continuity.

Install it in a test project:

```text
dotnet add package KeelMatrix.KeyRingGuard
```

First success:

```csharp
var factory = new KeyRingProviderFactory(
    _ => DataProtectionProvider.Create(
        keyStorePath,
        builder => builder.SetApplicationName("Sample.App")),
    TimeSpan.FromSeconds(5));

var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.RestartContinuity,
    factory);

Assert.True(result.Succeeded, result.Message);
```

KeyRingGuard is offline and has no telemetry dependency. Your provider factory owns its backing store and may intentionally contact a network provider. Use a temporary or dedicated store: Data Protection can auto-generate keys, so a verification can mutate a real store.

The configured timeout applies to each provider, protector, protect, unprotect, and rotation operation. Synchronous callbacks run on a worker so the verifier can return a bounded timeout result, but an already-running synchronous callback cannot be forcibly interrupted and may finish in the background. Cancellation is cooperative for asynchronous callbacks.

See the [installation and scenario guide](docs/usage.md) for replica sharing, isolation, rotation, provider factories, cleanup, and safe network-backed testing. See [what it proves](docs/what-it-proves.md) for the verification boundary.
