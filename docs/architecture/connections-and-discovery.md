# Connections and Discovery

## Host profile

A host profile is local configuration, not proof of remote identity. It contains:

- `HostProfileId`.
- Display name.
- Hostname or IP address.
- SSH port, default 22.
- Username.
- Authentication method metadata and secret references.
- Optional tags and notes.
- Host-key policy state.
- Connection preferences and discovery refresh policy.
- Created/updated timestamps.

Profiles never contain plaintext passwords, private-key contents, or passphrases.

## Connection state machine

```text
Disconnected
 -> Resolving
 -> Connecting
 -> Negotiating
 -> VerifyingHostIdentity
 -> Authenticating
 -> Connected
 -> Discovering (overlay activity)
 -> Ready
```

Terminal/error transitions include `Cancelled`, `NetworkFailed`, `IdentityRejected`, `AuthenticationFailed`, `Interrupted`, and `Disposed`. A reconnect creates a new connection identity and does not reuse stale ready state without validation.

## Host-key verification

- First-seen key: show host, address, algorithm, SHA-256 fingerprint, and a clear trust choice.
- Trusted matching key: continue without prompting.
- Changed key: block by default and show old/new fingerprints with a strong warning. Replacement requires explicit user action and must be audited locally.
- Plugin code cannot accept or replace a host key.
- A global insecure ignore option is prohibited.
- Hostname and address changes require re-evaluation of stored identity association.

## Authentication

Initial methods:

- Password.
- Private key, optionally protected by passphrase.

Authentication secrets are requested from `ISecretStore` or a core-owned secure prompt. The transport receives secret material only for the minimum lifetime required. Best-effort clearing/disposal is required where runtime types permit it.

Authentication failure messages must not reveal whether a particular username or credential exists beyond information already exposed by SSH.

## Connection manager

The connection manager:

- Owns transport instances and state transitions.
- Publishes immutable state snapshots/events.
- Provides bounded connection/channel leases to application services.
- Detects interruption and invalidates leases.
- Prevents plugins from holding transport objects.
- Supports explicit disconnect and application shutdown cleanup.
- Uses bounded retries only for safe connection-establishment phases and never loops indefinitely.

## Discovery plan

Discovery consists of independent probes executed through `ICommandExecutor` with `InvocationSource.Discovery`. Probe failures are captured without aborting unrelated probes.

Initial probes should determine when available:

- `uname` kernel, OS, release, and architecture.
- `/etc/os-release` facts.
- FreeBSD version facts.
- macOS `sw_vers` facts.
- Current shell and POSIX shell availability.
- Effective user ID and groups.
- Availability/version of `sudo` and `doas`.
- Init/service managers: systemd, OpenRC, rc.d, launchd.
- Package managers: apt/dpkg, dnf/rpm, yum, pacman, zypper, apk, pkg, brew.
- Date/time tools: timedatectl, date, systemsetup where applicable.
- Container tools: docker, podman.
- Basic system facts such as hostname and uptime.

Probes must avoid mutation and interactive commands.

## Facts and capabilities

Facts are normalized key/value observations, for example:

- `os.family=linux`
- `os.distribution=ubuntu`
- `kernel.arch=x86_64`
- `tool.systemctl.version=...`

Capabilities are stable semantic identifiers derived from facts and successful provider probes, for example:

- `shell.posix`
- `privilege.sudo`
- `init.systemd`
- `service.manage`
- `time.read`
- `time.timezone.write`

Capability IDs are lowercase dotted identifiers. A capability may include optional version metadata in the registry but the identifier itself is version-independent.

## Snapshot behavior

A discovery snapshot is immutable and contains:

- Snapshot ID, host profile ID, connection ID.
- Started/completed timestamps.
- OS family and normalized version facts.
- Facts with probe provenance.
- Capabilities and provider candidates.
- Per-probe outcome.
- Partial/complete status.

The latest successful facts may be cached for disconnected display, but the UI must mark them stale and must not use stale capability state to authorize execution. Execution revalidates current connection and relevant provider assumptions.

## Refresh rules

- Initial successful connection triggers discovery unless a product setting explicitly asks first.
- Manual refresh is available.
- Material host-key or OS identity change invalidates previous discovery.
- Reconnect may reuse recent display data while background refresh runs, but mutation remains blocked until required capabilities are confirmed.
- Plugins can contribute probes through constrained metadata and command factories, not direct transport calls.

## Provider selection

Provider selection considers:

1. Required capabilities.
2. Provider-declared compatibility predicates over normalized facts.
3. Provider priority/specificity.
4. Deterministic tie-breaking by plugin/provider ID.

Selection outcome records why a provider matched or why no provider is available. Ambiguous equal-priority providers are treated as a configuration/development error rather than selected nondeterministically.

## Testing requirements

Use fake transport fixtures for Linux/systemd, Linux/OpenRC, FreeBSD, macOS, partial discovery, command-not-found, permission denied, malformed output, slow probe, cancellation, and changed identity scenarios.