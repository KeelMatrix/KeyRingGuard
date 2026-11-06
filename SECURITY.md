# Security Policy

## Reporting a Vulnerability

Report suspected vulnerabilities privately before any public disclosure:

1. Email **keelmatrix@gmail.com**.
2. Open a private GitHub security advisory in this repository.

Do not create a public issue or otherwise publicly disclose sensitive vulnerability details, exploit steps, credentials, customer data, key material, or private reports.

Please include, when safe:

- affected KeyRingGuard version, runtime, operating system, and Data Protection/provider combination;
- safe reproduction steps or a minimized proof of concept;
- security impact and the affected trust boundary;
- whether the issue involves diagnostics, package contents, timeout behavior, or provider integration;
- suggested mitigation or fix, if known.

Reports are investigated best-effort. Ordinary bugs should use the normal project contribution path; Code of Conduct concerns should use the community reporting route.

## Supported Versions

Security fixes are prioritized for the latest released package line. Older versions may receive fixes on a case-by-case basis.

## Product Security Notes

KeyRingGuard generates only synthetic in-memory canaries. It does not read or emit Data Protection key XML, and it never deletes or revokes keys. Data Protection can auto-generate keys during provider initialization; a check against a real shared store may therefore mutate that store. `KeyManagementOptions.AutoGenerateKeys` controls the caller's provider behavior. Run checks against temporary or dedicated stores by default. Timeout observation includes lifecycle admission and a bounded cleanup wait. At most 32 verification lifecycles retain providers through operations, owned cancellation notifications, and best-effort cleanup. Stop results signal the active operation token without waiting for arbitrary registrations. The 32 primary callback slots are separate from asynchronous notification dispatch, with at most one unfinished dispatch per terminating lifecycle. Already-running callbacks, cancellation handlers, and Dispose cannot be forcibly interrupted; they retain capacity until they return. Cleanup timeout or exceptions preserve the scenario verdict, while caller cancellation during the cleanup wait returns `Canceled`. Cleanup re-entry on flowed execution context is rejected; deliberately severed flow remains unsupported. See the authoritative [API lifecycle contract](src/KeelMatrix.KeyRingGuard/KeyRingVerifier.cs). Isolation accepts a cross-boundary `CryptographicException` only from the standard ASP.NET Core `IPersistedDataProtector` contract after the same-boundary controls pass; arbitrary provider exceptions fail closed as `Unprotect`. Rotation additionally invokes an explicit caller-supplied key-creation callback, snapshots opaque key IDs in the same bounded scheduled operation immediately before invoking it after provider and key-manager setup succeeds, rejects a callback that returns a pre-existing key, and accepts only one matching observed key that is active and not revoked. A missing, truncated, or otherwise noncanonical recreated payload produces `RotationUnavailable`; a canonical payload naming the wrong or stale key produces `RotationFailure`. The callback must honor cancellation before and during the mutation, and must use the same provider/key-manager backing store. A caller-supplied provider factory remains responsible for its backing store, credentials, transport security, and access controls.
