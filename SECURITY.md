# Security policy

Please report security issues privately to the repository maintainers rather than opening a public issue with exploit details or secret material.

KeyRingGuard does not read or emit Data Protection key XML and never deletes or revokes keys. Run checks against temporary or dedicated stores. A caller-supplied provider factory remains responsible for its backing store, credentials, transport security, and access controls.
