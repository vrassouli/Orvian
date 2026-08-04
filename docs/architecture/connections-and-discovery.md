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
The MVP host editor exposes the validated connection timeout and maximum
additional network retry count rather than silently fixing every profile to the
defaults.

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
- Hostname, port, and resolved-address changes require re-evaluation of the
  stored identity association even when the key fingerprint is unchanged.

Migration 9 adds the last explicitly trusted resolved IP address to each
host-key record. Legacy records have no address and therefore require one
explicit revalidation on their next connection; they are not silently treated
as matching. Trust replacement stores the newly observed hostname, port,
address, algorithm, and SHA-256 fingerprint as one association.

Migration 10 adds immutable local host-trust audit events. First trust and
explicit replacement persist the trusted identity and its event in one SQLite
transaction; if either write fails, neither change commits and connection
establishment stops before another transport attempt. Activity inspection shows
these records as local security events. The event contains identity metadata and
fingerprints, but no credentials, secret references, or transport details.

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
- Enforces one establishment workflow per host; UI re-entry cancels the active
  workflow rather than queueing another attempt behind the host lock.

`MaximumReconnectAttempts` is the number of additional transport attempts after
the initial attempt, with a validated range of zero through five. The MVP
retries only `NetworkFailed` results during establishment, using bounded
exponential delays of 250 ms through 2 seconds. The retry budget is shared
across the complete establishment flow, including a second connection after an
explicit host-key trust decision. Authentication failure, identity decisions,
identity rejection, generic negotiation failure, and cancellation are never
retried automatically. Cancellation during transport or backoff terminates the
workflow before another attempt. Exhaustion reports the exact bounded attempt
count in safe UI-visible failure text.

## Discovery plan

Discovery consists of independent probes executed through `ICommandExecutor` with `InvocationSource.Discovery`. Probe failures are captured without aborting unrelated probes.

Optional provider/tool probes distinguish an unavailable command from a failed
discovery run. A clean non-zero result from an optional availability probe is
recorded as `Unsupported`; malformed output, timeout, cancellation, audit
failure, and unexpected execution failure remain real probe failures. An
unsupported optional provider does not by itself make the snapshot partial.
Availability/version probes accept bounded multi-line output because common
tools such as `systemctl`, `sudo`, and `apt-get` report feature or module lines
after their version header. They require non-whitespace content, reject NUL,
and cap parser input at 64 KiB; single-value fact probes retain strict
single-line parsing.

Initial probes should determine when available:

- `uname` kernel, OS, release, and architecture.
- `/etc/os-release` facts.
- FreeBSD `freebsd-version` facts.
- macOS `sw_vers` product name and version facts.
- Current shell and POSIX shell availability.
- Effective user ID and groups.
- Availability/version of `sudo` and `doas`.
- Init/service managers: systemd, OpenRC, rc.d, launchd.
- Package managers: apt/dpkg, dnf/rpm, yum, pacman, zypper, apk, pkg, brew.
- Date/time tools: timedatectl, date, systemsetup where applicable.
- Container tools: docker, podman.
- Basic system facts such as hostname and uptime.

Probes must avoid mutation and interactive commands.

The MVP implements these as independent bounded commands. FreeBSD rc.d
detection accepts only canonical `/etc/rc.d/` or `/usr/local/etc/rc.d/`
inventory paths before granting `init.rcd`; unexpected paths are a visible
parse failure. Group output is single-line, bounded to 256 group names, and
defensively validated. Missing optional platform tools are `Unsupported` and do
not make a snapshot partial, while malformed or truncated output remains a real
partial-discovery condition.

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
- `time.datetime.write`

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
- Manual refresh is available while the connection is ready. It runs through
  the same command/audit pipeline, preserves the previous cached snapshot on
  failure, and restores a usable connection state after cancellation or
  failure.
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
