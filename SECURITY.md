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

KeyRingGuard generates only synthetic in-memory canaries. It does not read or emit Data Protection key XML, and it never deletes or revokes keys. Data Protection can auto-generate keys during provider initialization; a check against a real shared store may therefore mutate that store. `KeyManagementOptions.AutoGenerateKeys` controls the caller's provider behavior. Run checks against temporary or dedicated stores by default. Rotation additionally invokes an explicit caller-supplied key-creation callback; it must honor cancellation before and during the mutation, and must use the same provider/key-manager backing store. A caller-supplied provider factory remains responsible for its backing store, credentials, transport security, and access controls.
