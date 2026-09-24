# Changelog

## [Unreleased]

### Changed

- Synchronous provider, protector, protection, unprotection, and rotation operations now return bounded timeout results; already-running synchronous callbacks cannot be forcibly interrupted and may finish in the background.
- Isolation checks now treat only expected cryptographic rejection as a passing verdict and fail closed on unrelated provider errors.

### Added

- Fail-closed package icon, structured vulnerability, changelog/version, and tag-only release-contract checks.
