# What KeyRingGuard proves

## Supported runtime and platforms

KeyRingGuard targets `net8.0`. Windows is the locally evidenced platform for this candidate; Linux and macOS are not yet evidenced here.

## Safety and privacy

KeyRingGuard proves the behavior of the exact provider factories supplied to the scenario:

- a payload can survive independently recreated providers against the configured store;
- two configured replicas can exchange payloads in both directions;
- intentionally different application discriminators reject cross-provider payloads;
- different purposes reject cross-purpose payloads;
- a payload remains readable after an explicitly requested, non-destructive key creation step when a key-manager factory is supplied;
- provider construction, protector creation, protect, unprotect, and key-manager operations are observed with the caller's timeout and cancellation boundary. Synchronous callbacks cannot be forcibly interrupted after they start and may finish in the background; asynchronous cancellation remains cooperative.

KeyRingGuard does not prove that a store has a valid backup, disaster-recovery process, HSM configuration, access-control policy, network reliability beyond the exercised operation, key escrow, or production-wide fleet consistency. It is not a key vault, a backup service, a replacement cryptography implementation, a CVE scanner, a health endpoint, or an authentication-cookie parser.

## Release portability evidence

The release portability contract proves only that PowerShell source text contains no literal Windows path separator. It checks every `.ps1`, `.psm1`, and `.psd1` source file under the repository root outside `.git` metadata, with an empty allowlist.

It explicitly does not prove release-job portability for `.json`, `.props`, `NuGet.config`, or separators constructed at runtime. End-to-end release-path portability remains tied to a founder-gated Linux run.
