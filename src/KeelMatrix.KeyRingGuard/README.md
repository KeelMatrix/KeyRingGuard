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

Read the [scenario guide](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/usage.md) and [verification boundary](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/what-it-proves.md).
