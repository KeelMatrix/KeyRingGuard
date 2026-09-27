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
dotnet pack src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj --configuration Release --no-build --include-symbols --p:SymbolPackageFormat=snupkg --output ./artifacts/packages
pwsh ./scripts/Invoke-LocalGate.ps1
```

## Invariants

- The shipping target framework is `net8.0`.
- Provider construction is caller-owned; the library never creates an implicit fallback provider.
- Verification uses synthetic, ephemeral canaries and fixed diagnostics. It never reads, writes, or emits key XML or master key material.
- No verifier operation deletes or revokes keys. Rotation verification requires an explicitly supplied key-manager factory and a dedicated test store.
- The package has no network behavior and no telemetry dependency. A caller-supplied provider factory may use a network-backed store.
- Test projects and the package consumer are not packable and must not become package dependencies.

## Validation strategy

Use the smallest affected test project first. Before handoff, run the local validation path once, inspect the `.nupkg` and `.snupkg`, and run the package consumer from an isolated local feed. Public CI additionally proves the claimed Windows/Linux/macOS matrix; package inspection must not be skipped when the founder-owned icon is absent.

## Scope boundaries

Keep changes within the verifier, its tests, packaging, and the documentation needed to describe shipped behavior. Do not add provider SDKs, a CLI, hosted services, key-management operations, authentication-cookie parsing, or unrelated repository automation.
