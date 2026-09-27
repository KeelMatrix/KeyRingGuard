# What KeyRingGuard proves

## Supported runtime and platforms

KeyRingGuard targets `net8.0`. The repository's filesystem integration and package-consumer checks run on Windows, Linux, and macOS in the release-equivalent CI matrix. That evidence covers the shipped filesystem fixtures, not every third-party provider or deployment environment.

## Safety and privacy

KeyRingGuard proves the behavior of the exact provider factories supplied to the scenario:

- a payload can survive independently recreated providers against the configured store; a factory that reuses one live provider instance is rejected;
- two configured replicas can exchange payloads in both directions;
- intentionally different application discriminators reject cross-provider payloads only after caller-supplied factories declare the same `KeyRingBoundary` and the control providers pass their same-boundary exchange;
- different purposes reject cross-purpose payloads;
- an explicitly requested key rotation uses linked provider/key-manager factories and a cancellation-aware key-creation callback, observes a new active key, proves a recreated provider names that key in a fresh protected payload, and keeps both old and new payloads readable;
- provider construction, protector creation, protect, unprotect, key observation, and key-manager operations are observed with the caller's timeout and cancellation boundary. Synchronous callbacks run through a bounded scheduler but cannot be forcibly interrupted after they start and may finish in the background; asynchronous cancellation remains cooperative, and late provider results are disposed.

Data Protection can auto-generate keys during provider initialization. A check against a real shared store may therefore mutate that store; `KeyManagementOptions.AutoGenerateKeys` controls the caller's provider behavior. Use temporary or dedicated stores by default and make deliberate shared-store mutation explicit.

KeyRingGuard does not prove that a store has a valid backup, disaster-recovery process, HSM configuration, access-control policy, network reliability beyond the exercised operation, key escrow, or production-wide fleet consistency. It is not a key vault, a backup service, a replacement cryptography implementation, a CVE scanner, a health endpoint, or an authentication-cookie parser.
