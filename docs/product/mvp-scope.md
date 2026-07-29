# MVP Scope

## Objective

Deliver a trustworthy desktop application that proves Orvian's complete vertical architecture on real SSH-managed systems: local host inventory, verified connection, discovery, capability-based UI, plugin-provided features, structured execution, privilege escalation, and redacted audit history.

## Included

### Desktop application

- Avalonia shell for Windows, macOS, and Linux.
- Host inventory and connection editor.
- Host selector, navigation, content region, activity access, settings, dialogs, and notifications.
- Local SQLite persistence and platform secret-store integration.

### Connections

- SSH hostname/IP and configurable port.
- Username authentication with password or private key.
- Passphrase-protected keys.
- Mandatory unknown/changed host-key flow.
- Explicit connect, disconnect, reconnect, and cancellation.
- One active logical connection per selected host; pooling and multi-host concurrency are implementation details bounded by policy.

### Discovery

- OS family, distribution/version when available, kernel, architecture, shell.
- Detection of `sudo`, `doas`, init systems, package managers, and feature executables.
- Normalized capabilities and facts with timestamps.
- Manual refresh and refresh after material reconnect.

### Core execution

- Structured command request/result contracts.
- Command validation, escaping, timeout, cancellation, output truncation, redaction, privilege handling, and auditing.
- Operations grouping multiple commands.
- In-memory execution prototypes replaced by SSH.NET transport before MVP release.

### Plugin platform

- Manifest validation.
- Application-controlled plugin directory.
- Enable/disable state.
- Version and compatibility checks.
- Capability requirements and provider selection.
- Navigation/page/command/settings contributions.
- Plugin permissions enforced by the core.
- First-party plugins built with the same public contracts intended for third parties.

### First-party features

- Host Overview: identity, OS, uptime, architecture, discovery facts, and connection state.
- Date & Time: inspect time, timezone, synchronization state; update timezone/time using supported providers.
- Services: list, inspect, start, stop, restart, enable, and disable when supported; risky actions require confirmation.
- Users & Groups: read-only listing and detail view for MVP. Mutation is deferred unless all security and portability requirements are completed early.
- Package Information: identify package manager and show selected installed-package facts; full package installation/removal is deferred.

### Audit and diagnostics

- Persistent operation and command records.
- Distinct audit and diagnostic views or filters.
- Redacted command details, status, timing, exit code, bounded output, and failure classification.
- Search and filtering by host, plugin, date, source, and status.

### Quality

- Automated unit tests for contracts, validation, redaction, provider selection, and security-sensitive code.
- Integration tests using controllable fake transport.
- CI build/test on supported development runner platforms where practical.
- User-visible failure states and no unobserved task exceptions.

## Explicitly excluded from MVP

- Windows host administration.
- WinRM, RDP, SNMP, NETCONF, REST appliance APIs, or protocols other than SSH.
- Required remote agent installation.
- Cloud accounts, synchronization, teams, RBAC, centralized policy, or hosted control plane.
- Plugin marketplace, remote registry, payments, signing infrastructure, or automatic plugin updates.
- Strong plugin sandboxing or process isolation.
- Fleet-wide batch execution and orchestration.
- Scheduling, runbooks, workflows, configuration drift, desired-state management, or unattended automation.
- Full terminal emulator.
- File transfer UI, SCP/SFTP browser, port forwarding, tunnels, or remote desktop.
- Full monitoring, alerting, dashboards, metrics retention, or log aggregation.
- AI features. Future AI actions must use the same operation pipeline and are not allowed to bypass confirmation or policy.
- Audit export to SIEM, syslog, or OpenTelemetry backends.
- Tamper-evident hash chains and centralized append-only audit storage.
- Package installation/removal and user mutation unless separately promoted into scope by an accepted decision.

## Supported-host definition

A host is supported for the MVP when it:

- Is reachable over SSH.
- Presents a POSIX-compatible remote command environment.
- Can execute discovery probes with the authenticated account.
- Matches at least one implemented provider for the requested feature.

A supported OS family does not guarantee every feature. Availability is decided per capability and provider.

## Release acceptance scenario

On a clean workstation, a user must be able to:

1. Launch Orvian and create a host profile without storing a plaintext secret.
2. Connect to a Linux host and explicitly trust its first-seen host key.
3. Authenticate and complete partial or full discovery.
4. See an overview and only compatible feature pages.
5. Inspect date/time and service information.
6. Request a service restart or timezone change.
7. Review a confirmation showing host, purpose, privilege, and risk.
8. Supply privilege credentials through a core-owned prompt when required.
9. Receive a clear success or failure result.
10. Inspect operation and command audit entries with secrets redacted.
11. Disconnect and reconnect without corrupting state or leaking credentials.

## Scope-change rule

An agent may not add excluded functionality because it seems convenient. Scope changes require an explicit issue or accepted ADR that updates this document and the relevant requirements.