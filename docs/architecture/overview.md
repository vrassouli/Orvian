# Architecture Overview

## System shape

Orvian is a local-first modular desktop application. The executable hosts the shell and core platform services. Feature plugins are loaded into the application process after manifest and compatibility validation.

```text
Avalonia Shell
  -> Application Services
     -> Plugin Host and Contribution Registry
     -> Host Inventory and Persistence
     -> Connection Manager
     -> Discovery and Capability Registry
     -> Operation and Command Pipeline
        -> Policy and Permission Checks
        -> Audit Start
        -> Privilege Provider
        -> SSH Transport
        -> Output Handling and Parsing
        -> Audit Completion
     -> Secret Store
```

## Architectural layers

### Presentation

Avalonia views, view models, navigation, dialogs, notifications, commands, and presentation models. Presentation depends on application contracts and does not access transport or persistence implementations directly.

### Application

Coordinates user use cases: create host, connect, discover, execute an operation, load plugins, and inspect audit history. Application services define transaction and orchestration boundaries.

### Domain and contracts

Stable models and interfaces for hosts, capabilities, plugins, operations, commands, audit, permissions, and secrets. Contracts must not depend on Avalonia, SSH.NET, SQLite, or operating-system-specific implementations.

### Infrastructure

SSH.NET transport, SQLite repositories, platform secret stores, file-based plugin discovery, logging, clock, process/environment integration, and platform packaging.

### Plugins

Feature behavior, provider implementations, output parsing, capability requirements, and UI contributions. Plugins depend only on published Orvian contracts and explicitly allowed UI abstractions.

## Dependency direction

- Shell may reference application and UI contracts.
- Application may reference domain contracts.
- Infrastructure implements domain/application interfaces.
- Plugin abstractions reference only stable platform-neutral contracts.
- First-party plugins reference plugin abstractions and approved UI packages.
- Core projects never reference a concrete feature plugin.
- Plugins never reference infrastructure implementations such as SSH.NET sessions or SQLite contexts.

## Runtime composition

The application composition root owns dependency injection and creates all infrastructure implementations. Plugins receive a constrained registration builder and runtime service interfaces. They never receive the root service provider for arbitrary resolution.

## Major aggregates and identities

- `HostProfileId`: stable local identity of a configured host.
- `HostIdentity`: verified remote key/fingerprint information associated with a profile.
- `ConnectionId`: one connection lifecycle instance.
- `DiscoverySnapshotId`: immutable result of one discovery run.
- `PluginId`: globally stable reverse-domain or Orvian-defined identifier.
- `OperationId`: one user/background intention, possibly containing several commands.
- `CommandId`: one attempted remote command.
- `AuditEventId`: persistence identity for an audit transition or record.

Use typed identifiers or strongly constrained value objects where practical. Do not use display names as identities.

## State ownership

- Host profile state: host inventory service and repository.
- Secrets: platform `ISecretStore`; records contain only secret references.
- Connection state: connection manager; not persisted as authoritative state.
- Discovery facts and capabilities: discovery service, snapshot repository, capability registry.
- Plugin enabled state: plugin catalog repository.
- Operation progress: operation coordinator; terminal result persisted to audit store.
- UI navigation selection: shell state.

## Concurrency rules

- UI thread is never blocked on network, database, plugin loading, or command execution.
- One command may execute at a time per SSH channel. Connection manager may provide multiple channels under a bounded policy.
- Mutating operations targeting the same host and resource should be serialized by an operation lock key when a feature supplies one.
- Read-only refreshes may run concurrently when transport capacity permits.
- Cancellation is propagated from UI to application service to command pipeline to transport.
- Persistence writes use transactions for logically atomic changes.

## Error taxonomy

Errors are classified, not flattened into strings:

- Validation error.
- Policy or permission denial.
- Secret unavailable.
- DNS or network failure.
- SSH negotiation failure.
- Host identity unknown or changed.
- Authentication failure.
- Connection interruption.
- Command timeout or cancellation.
- Privilege denial.
- Nonzero remote exit.
- Output truncation.
- Parsing failure.
- Plugin load or compatibility failure.
- Persistence failure.
- Unexpected internal failure.

User-facing messages are derived from structured failures. Raw exceptions remain in local diagnostic logs after redaction.

## Extension model

A feature is separated into:

- Feature definition: user-facing concept and required capabilities.
- Provider: target-specific implementation selected from host facts/capabilities.
- Contributions: pages, commands, settings, widgets, and discovery probes.
- Permissions: declared operations the plugin may request.

For example, the Services feature can select a systemd, OpenRC, launchd, or future RouterOS provider without changing shell navigation.

## Architectural invariants

1. No plugin receives raw SSH sessions or secrets.
2. All remote execution uses the command pipeline.
3. All attempted commands are auditable.
4. User input is never concatenated into shell command text.
5. Capabilities and providers isolate platform differences.
6. Security policy cannot be overridden by plugin UI.
7. Persistence records never contain plaintext credentials.
8. UI availability does not imply permission to execute; execution is revalidated.
9. Background and AI sources use the same pipeline as direct UI actions.
10. Public contracts change only with compatibility consideration and documentation.