# NixOS VM test fixtures

`lab-builder.key` and `lab-builder.pub` are a **publicly known, test-only**
Ed25519 builder key pair. The Guardian VM tests use them to sign attestations
inside disposable QEMU guests. Never list this public key in a real system's
`services.nexus-guardian.trustedBuilderKeys`: anyone can sign with it.
