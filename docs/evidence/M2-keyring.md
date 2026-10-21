# M2-1 Linux Secret Service acceptance (PR #54)

This gate exercises a **real GNOME Secret Service daemon in a disposable Linux CI session**, with synthetic test credentials only. The HTTP response is supplied by an in-process fixture; no live cloud provider, production user keyring, hardware or external network was tested.

## Reproduce on Ubuntu 24.04

Install .NET 8 SDK and the dependencies below, then from the repository root:

```bash
sudo apt-get update
sudo apt-get install -y --no-install-recommends dbus-x11 gnome-keyring libsecret-tools libglib2.0-bin
dotnet build tests/NexusOS.Tests/NexusOS.Tests.csproj --configuration Debug
bash scripts/ci/real-secret-service.sh
```

The script uses disposable HOME, XDG_CONFIG_HOME, XDG_DATA_HOME and XDG_RUNTIME_DIR directories (umask 077), starts a private D-Bus session, initialises GNOME Keyring via its login/start sequence, and requires a synthetic secret-tool store/lookup preflight. It runs the unlocked/unavailable cases, then locks the default collection through Secret Service and runs the locked cases. Failure of the preflight is a setup failure, not an adapter assertion failure.

## Regression and CI evidence (21 October 2026)

- [Real Secret Service #8](https://github.com/TheMatthewWalker/NexusOS/actions/runs/39612923541): red, 3 of 4 tests passed; missing-secret adapter test failed because secret-tool clear returned a nonzero code when the entry was already absent.
- [Fix 83835ea](https://github.com/TheMatthewWalker/NexusOS/commit/83835ea62a84f938dc8bcd5d61bd84615fa0322d): remove the incorrect prerequisite clear from the missing-secret test.
- [Real Secret Service #9](https://github.com/TheMatthewWalker/NexusOS/actions/runs/39621874035): green; 4 unlocked/unavailable tests and 2 locked tests passed, including adapter Authorization-header propagation and no outbound HTTP call for missing or locked secrets.
- [Core CI #173](https://github.com/TheMatthewWalker/NexusOS/actions/runs/39621874180): green.
- [Guardian Validation #145](https://github.com/TheMatthewWalker/NexusOS/actions/runs/39621874044): green.

Unit/helper tests separately cover absent secret-tool, malformed credentials and excessive output; these **are not real-keyring evidence**. CI uses synthetic credentials and a local HTTP handler rather than a real cloud service. No Guardian host activation or real-hardware acceptance was performed.

## Remaining gate

Independent security review and approval are required before merging #54. Review should verify the fail-closed exception type, bounded process cleanup, no plaintext environment fallback, no sensitive error/log leakage, and the distinction between real Secret Service access and fixture-based HTTP. Do not begin #51 until #50 merges with passing checks.
