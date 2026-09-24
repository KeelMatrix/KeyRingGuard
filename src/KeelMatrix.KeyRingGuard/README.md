# KeelMatrix.KeyRingGuard

Prove that an ASP.NET Core Data Protection payload survives restart, replica, and deterministic rotation transitions—and that intentionally isolated application or purpose boundaries reject it.

Install:

```text
dotnet add package KeelMatrix.KeyRingGuard
```

First success:

```csharp
var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.RestartContinuity,
    new KeyRingProviderFactory(
        _ => DataProtectionProvider.Create(
            keyStorePath,
            builder => builder.SetApplicationName("Sample.App")),
        TimeSpan.FromSeconds(5)));

Assert.True(result.Succeeded, result.Message);
```

KeyRingGuard is offline and has no telemetry dependency. Use a temporary or dedicated test store because Data Protection may auto-generate a key during verification.

The configured timeout applies to each provider, protector, protect, unprotect, and rotation operation. Synchronous callbacks run on a worker so the verifier can return a bounded timeout result, but an already-running synchronous callback cannot be forcibly interrupted and may finish in the background. Cancellation is cooperative for asynchronous callbacks.

Read the [scenario guide](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/usage.md) and [verification boundary](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/what-it-proves.md).
