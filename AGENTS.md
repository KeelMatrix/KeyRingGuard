# Developer Guide

## Navigation

- Public API and verifier behavior: `src/KeelMatrix.KeyRingGuard/`
- Behavioral tests: `tests/KeelMatrix.KeyRingGuard.UnitTests/`
- Filesystem/provider integration tests: `tests/KeelMatrix.KeyRingGuard.IntegrationTests/`
- Package consumer: `tests/KeelMatrix.KeyRingGuard.PackageConsumer/`
- Local validation and package inspection: `scripts/`
- User documentation: `docs/`

Read the shipping project before changing the verifier, then the nearest matching test. Keep the package provider-neutral and inspect the actual package before changing packaging metadata.

## Commands

```text
dotnet restore KeelMatrix.KeyRingGuard.sln
dotnet build KeelMatrix.KeyRingGuard.sln --configuration Release --no-restore
dotnet test KeelMatrix.KeyRingGuard.sln --configuration Release --no-build
dotnet pack src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj --configuration Release --no-build --include-symbols --p:SymbolPackageFormat=snupkg -p:RequirePackageIcon=false --output ./artifacts/packages
pwsh ./scripts/Invoke-LocalGate.ps1
```

## Invariants

- The shipping target framework is `net8.0`.
- Provider configuration is caller-owned; the library never creates an implicit fallback provider.
- Application-isolation factories must share an explicit `KeyRingBoundary` identity with their control factories.
- Verification uses synthetic, ephemeral canaries and fixed diagnostics. It never reads, writes, or emits key XML or master key material.
- No verifier operation deletes or revokes keys. Rotation verification requires explicitly supplied linked key-manager and cancellation-aware key-creation factories, snapshots opaque key IDs and invokes the callback in one bounded scheduled operation after setup succeeds, rejects pre-existing/no-op key returns, and uses a dedicated test store.
- The class XML remarks in `src/KeelMatrix.KeyRingGuard/KeyRingVerifier.cs` own the timeout/cleanup/ownership contract. At most 32 verification lifecycles retain providers until operations, owned cancellation notifications, and best-effort cleanup finish. Stop results signal the active operation token without joining consumer handlers; at most one unfinished asynchronous notification dispatch is retained per terminating lifecycle outside the primary slots. Provider, key-manager, rotation, and cleanup callbacks share 32 bounded scheduler slots; result observation includes a bounded cleanup wait, and caller cancellation during that wait returns `Canceled`. A nested KeyRingGuard verification started on the callback's or cancellation handler's own flowed execution context returns `InvalidScenario` before scheduler admission, and queued admission honors the caller's cancellation token. A callback that deliberately severs flow with `ExecutionContext.SuppressFlow`, `ThreadPool.UnsafeQueueUserWorkItem`, or a new thread is outside this guard and unsupported.
- Isolation verdicts require the same-boundary control to pass and the cross-check protector to implement the standard ASP.NET Core persisted-protector contract; arbitrary provider exceptions fail closed. The rotation contract in `docs/what-it-proves.md` defines canonical envelope support: the little-endian key ID and a non-empty encryptor-specific payload after the standard `09 F0 C9 F0` header. Canonical bytes remain supported when the alternate byte order is identical.
- The package has no network behavior and no telemetry dependency. A caller-supplied provider factory may use a network-backed store.
- Test projects and the package consumer are not packable and must not become package dependencies.
- Readiness packaging allows the founder-owned root icon to be absent and verifies it whenever present; release packaging passes `RequirePackageIcon=true` and fails closed when the icon is absent.

## Validation strategy

Use the smallest affected test project first. Before handoff, run the local validation path once, inspect the `.nupkg` and `.snupkg`, and run the package consumer from an isolated local feed. Public CI additionally proves the claimed Windows/Linux/macOS matrix; package inspection must not be skipped when the founder-owned icon is absent.

The deterministic source-path settings in `Directory.Build.props` use the SDK's repository-root mapping. Package inspection checks every non-embedded PDB source URL and checksum against the recorded Git tree, separately verifies embedded generated sources, and rejects private document paths. `tests/VerifySourceLinkContract.ps1` exercises these artifact checks and their negative controls.

`scripts/Test-HistoryHygiene.ps1` defines the history identity contract: responsible authors retain the company/Dependabot allowlist; the technical committer may also be exactly `GitHub <noreply@github.com>`. Attribution and all reachable-history checks still apply to web commits and merges. `tests/VerifyHistoryHygieneContract.ps1 -IdentityOnly` runs the focused stored-object identity matrix.

## Scope boundaries

Keep changes within the verifier, its tests, packaging, and the documentation needed to describe shipped behavior. Do not add provider SDKs, a CLI, hosted services, key-management operations, authentication-cookie parsing, or unrelated repository automation.
