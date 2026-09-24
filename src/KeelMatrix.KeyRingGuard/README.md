# KeelMatrix.KeyRingGuard

Prove that an ASP.NET Core Data Protection payload survives restart, replica, and deterministic rotation transitions—and that intentionally isolated application or purpose boundaries reject it.

## Install

Install:

```text
dotnet add package KeelMatrix.KeyRingGuard
```

## Quick Start

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

## Supported runtime and platforms

The package targets `net8.0`. Windows is supported and tested here. Linux and macOS are supported by the package, but filesystem behavior on those platforms has not yet been verified in this repository. The repository uses the .NET SDK `10.0.401`, pinned in `global.json`, to build the package.

## Safety and privacy

KeyRingGuard uses synthetic, ephemeral canaries only, never key material. It has no telemetry dependency and does not delete or revoke keys. Verification is non-destructive to existing keys; opt-in rotation creates one new key in the caller's dedicated test store. Read the [security policy](https://github.com/KeelMatrix/KeyRingGuard/blob/main/SECURITY.md) and [privacy policy](https://github.com/KeelMatrix/KeyRingGuard/blob/main/PRIVACY.md).

## Compatibility

The durable public API is pre-1.0. Breaking changes are possible before the first public release, and the shipping API baseline is enforced by public API analyzers.

## Scenarios and evidence

The configured timeout applies to each provider, protector, protect, unprotect, and rotation operation. Synchronous callbacks run on a worker so the verifier can return a bounded timeout result, but an already-running synchronous callback cannot be forcibly interrupted and may finish in the background. Cancellation is cooperative for asynchronous callbacks.

Read the [scenario guide](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/usage.md) and [verification boundary](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/what-it-proves.md).
