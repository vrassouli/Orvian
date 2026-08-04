# Product Requirements

## Functional requirements

### Host inventory

- Users can create, edit, duplicate, disable, and delete host profiles.
- A host profile contains a stable local identifier, display name, hostname or IP address, SSH port, username, authentication reference, optional tags, optional notes, and connection preferences.
- Secrets are referenced by opaque secret identifiers and are never stored in profile records.
- Deleting a profile must require confirmation and must not silently delete unrelated audit records.
- Hosts can be searched and filtered by name, address, tags, operating-system family, capabilities, and connection state.

### Connection establishment

- The user can initiate and cancel a connection.
- DNS, TCP, SSH negotiation, host-key verification, authentication, and discovery must expose distinct progress and error states.
- Host identity verification is mandatory. Unknown or changed keys require an explicit decision according to the host-key policy.
- Supported initial authentication methods are password and private key. Passphrase-protected private keys are supported through secure prompting.
- Secrets may be remembered only after explicit user choice and must be stored through `ISecretStore`.
- Reconnect behavior must be bounded and visible; no infinite retry loop.

### Discovery

- Successful authentication triggers or permits discovery of operating-system family, kernel, architecture, shell, distribution facts, init system, privilege tools, package managers, and known executables.
- Discovery produces normalized facts and capability identifiers.
- Discovery commands use the same command pipeline and auditing infrastructure but are classified as diagnostic activity.
- Partial discovery is valid. One failed probe must not discard successful facts.
- Facts carry provenance and freshness metadata so later refreshes can replace stale values.

### Capability-driven features

- Navigation shows a feature only when at least one compatible provider can satisfy its required capabilities.
- A feature may be available in read-only mode when mutation capabilities are absent.
- Provider selection is deterministic and explainable.
- Unsupported features clearly state why they are unavailable when surfaced through search or deep links.

### Operations

- A user action is represented as an operation that may contain one or more remote commands.
- Operations have stable identifiers, titles, descriptions, target host, source, risk level, privilege requirement, status, timestamps, and command children.
- Destructive or high-risk operations require explicit confirmation.
- Long-running operations show progress, allow cancellation when transport semantics permit it, and never block the UI thread.
- Batch operations across many hosts are outside the MVP, but the model must not prevent them later.

### Structured command execution

- Plugins submit an executable and structured arguments, not an interpolated shell string.
- The core validates plugin identity, declared permission, host state, argument policy, privilege policy, timeout, cancellation, and audit availability before transport execution.
- Each command receives a unique ID and belongs to an operation.
- Standard output and error are captured with configurable limits.
- Exit code, timeout, cancellation, transport interruption, privilege denial, policy denial, and parsing failure remain distinguishable.
- Commands that require shell syntax must use an explicit shell-execution abstraction with a documented reason; this is an exception, not the default path.

### Privilege escalation

- Plugins declare required privilege: user, elevated, or root-only.
- The core determines whether the current identity is already privileged or whether an approved escalation provider such as `sudo` or `doas` is available.
- Privilege credentials are never exposed to plugins and are never logged.
- Session-only caching is allowed after explicit authentication. Persistent storage of sudo passwords is prohibited in the MVP.
- Privilege prompts explain the operation and target host.
- A denied or failed escalation ends the command with a specific result.

### Audit and diagnostic activity

- Every attempted remote command creates a start record before execution and a completion record after execution or failure.
- Audit records include operation, host identity, user, plugin, plugin version, invocation source, executable, redacted arguments, privilege, timing, status, exit code, output metadata, and failure classification.
- User-facing audit and diagnostic activity are distinguishable but use a common underlying event model.
- Output storage follows the logging and redaction policy.
- Users can filter, inspect, and export audit records in a later milestone; inspect and filter are MVP requirements, export is not.

### Plugin management

- First-party and third-party plugins use the same lifecycle contracts.
- Plugins have manifests with stable ID, semantic version, publisher, compatible Remotune version range, entry assembly, permissions, capabilities, and contributions.
- Plugins can be discovered, validated, enabled, disabled, loaded, and unloaded when technically safe.
- Invalid, incompatible, duplicate, or permission-escalating plugins are quarantined with an explanatory error.
- The MVP loads plugins from a local application-controlled directory. Online registry and automatic update are later work.
- Disabling a plugin removes its UI contributions and stops its background work without requiring application restart when feasible.

### Application shell

- The shell provides host selection, primary navigation, contextual content, operation/activity access, notifications, settings, and connection status.
- Plugins contribute navigation and commands through metadata; they do not manipulate shell controls directly.
- The shell handles empty, loading, disconnected, unsupported, error, and permission-denied states consistently.
- Navigation state must not execute remote commands implicitly unless the page explicitly declares a refresh operation.

### Settings

- Settings include appearance, locale-ready formatting, connection defaults, output retention, audit retention, plugin state, and security preferences.
- Security-sensitive defaults must be conservative.
- Settings changes are validated and persisted atomically.

## Non-functional requirements

### Security

- No secret may appear in logs, exceptions shown to telemetry, audit arguments, crash reports, or plugin-visible objects.
- Plugin code runs in-process in the MVP and is therefore trusted code after installation; permissions reduce accidental and policy violations but are not a sandbox boundary.
- Host-key verification cannot be globally bypassed by a plugin.
- All input used in command construction is treated as untrusted.

### Reliability

- A plugin failure must not crash the entire application when containment is possible.
- Failed persistence writes must not be reported as successful operations.
- Audit start records must be attempted before transport execution; if mandatory audit storage is unavailable, mutation commands fail closed unless policy explicitly permits degraded diagnostic execution.
- Application startup should tolerate one corrupt plugin or one corrupt noncritical settings record.

### Performance

- Application startup target: interactive shell within 3 seconds on a typical modern workstation, excluding plugin discovery that can continue asynchronously.
- UI interactions should respond within 100 ms when no remote operation is required.
- Remote output is streamed or incrementally processed where supported to avoid large memory spikes.
- Default captured output limits: 1 MiB stdout and 256 KiB stderr, configurable by policy.

### Compatibility

- Desktop targets: Windows, macOS, and mainstream Linux desktop environments supported by Avalonia and .NET 10.
- Remote targets: Linux, FreeBSD, and macOS with SSH and a POSIX-compatible command environment for MVP providers.
- Public plugin contracts follow semantic versioning after first stable SDK release.

### Accessibility and localization

- All interactive controls support keyboard navigation and meaningful accessible names.
- Status is not conveyed by color alone.
- User-facing strings are localizable even though the initial UI language is English.
- Dates, times, numbers, and file sizes use culture-aware presentation while stored values remain invariant.

### Observability

- Local structured application logs are separate from remote command audit data.
- Correlation IDs connect UI operations, command execution, transport events, and audit records.
- Logging levels and categories allow troubleshooting without enabling secret exposure.

## Product constraints

- .NET 10 and Avalonia UI.
- MVVM for UI separation.
- SSH.NET for initial SSH transport unless an ADR replaces it.
- SQLite for local relational persistence unless an ADR replaces it.
- No mandatory cloud service.
- No mandatory managed-host agent.

## Release criteria for the first usable MVP

The MVP is releasable when a user can install Remotune, create a host profile, securely verify and connect to a supported Linux host, complete discovery, view a host overview, inspect services and date/time through plugins, execute at least one safe privileged change with confirmation, and review the full redacted audit trail without any plugin obtaining direct SSH or secret access.
