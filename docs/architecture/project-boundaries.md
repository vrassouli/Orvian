# Project Boundaries

## Intended solution structure

```text
src/
  Orvian.App
  Orvian.Shell
  Orvian.UI.Abstractions
  Orvian.Application
  Orvian.Core
  Orvian.Plugin.Abstractions
  Orvian.PluginHost
  Orvian.Connections
  Orvian.Ssh
  Orvian.Discovery
  Orvian.Security
  Orvian.Auditing
  Orvian.Persistence
plugins/
  Orvian.Plugins.HostOverview
  Orvian.Plugins.DateTime
  Orvian.Plugins.Services
  Orvian.Plugins.Users
  Orvian.Plugins.Packages
tests/
  ... corresponding unit, architecture, and integration projects
```

Projects may be introduced incrementally. Do not create a project solely to hold one class; preserve the responsibility boundaries below.

## Responsibilities

### Orvian.App

- Executable entry point.
- Composition root and dependency-injection registration.
- Platform initialization, application lifetime, crash boundary, and startup diagnostics.
- Must contain almost no product logic.

### Orvian.Shell

- Avalonia main window and shell view models.
- Host selection, navigation, activity access, dialogs, notifications, and settings entry.
- Renders registered contributions.
- Does not execute SSH commands directly.

### Orvian.UI.Abstractions

- Stable UI contribution contracts and platform-neutral presentation descriptors.
- Approved abstractions for plugin pages, commands, dialogs, and icons.
- Must not expose concrete shell controls or mutable navigation collections.

### Orvian.Application

- Use-case orchestration and application services.
- Transaction boundaries and coordination among repositories, connection manager, discovery, execution, and auditing.
- Returns structured outcomes suitable for UI presentation.

### Orvian.Core

- Platform-neutral domain models, value objects, command and operation contracts, shared result types, and capability identifiers.
- No references to Avalonia, SSH.NET, SQLite, or concrete plugins.

### Orvian.Plugin.Abstractions

- Plugin manifest, lifecycle, registration, permissions, providers, and contributions.
- Depends on stable Core and approved UI abstractions only.

### Orvian.PluginHost

- Plugin scanning, manifest reading, validation, dependency loading, lifecycle, contribution registration, quarantine, enable/disable, and compatibility checks.
- Does not implement feature behavior.

### Orvian.Connections

- Host profile connection orchestration, state machine, connection leases, retry policy, and lifecycle events.
- Depends on a transport abstraction; not on concrete feature plugins.

### Orvian.Ssh

- SSH.NET adapter and SSH-specific transport implementation.
- Host-key callbacks, authentication adapters, channels, command transport, cancellation, and low-level error mapping.
- Never exposes SSH.NET types outside the project.

### Orvian.Discovery

- Discovery plan, probes, fact normalization, capability resolution, snapshot creation, and refresh policy.
- Probes execute through the command pipeline.

### Orvian.Security

- Secret-store abstractions and platform implementations, policy evaluation, permission checks, redaction, confirmation classification, host-key policy, and privilege credential handling.

### Orvian.Auditing

- Operation/command audit model, persistence contracts, diagnostic classification, queries, retention, and output metadata.
- Does not contain transport execution.

### Orvian.Persistence

- SQLite schema, migrations, repositories, transactions, serialization, and recovery.
- Implements contracts defined in application/domain projects.

### First-party plugins

- Feature definitions and providers.
- Output parsing and mapping to feature models.
- UI contributions and feature view models.
- They must obey exactly the same execution and permission contracts expected of third-party plugins.

## Forbidden dependencies

- Any plugin -> Orvian.Ssh, Orvian.Persistence, SSH.NET, or a platform secret implementation.
- Core -> Avalonia, SSH.NET, SQLite, or plugin projects.
- Shell -> concrete plugin assemblies.
- Infrastructure project -> shell controls.
- Plugin -> root `IServiceProvider`.
- UI view model -> SSH.NET session or SQLite connection.

## Architecture tests

Automated architecture tests must eventually verify:

- Forbidden project references.
- Core assemblies do not reference infrastructure packages.
- Plugin assemblies do not reference SSH.NET or persistence packages.
- Concrete first-party plugins are not referenced by core projects.
- Public contract assemblies remain platform-neutral.

## Namespace convention

Namespace follows project ownership, for example:

- `Orvian.Core.Commands`
- `Orvian.Security.Redaction`
- `Orvian.Plugins.Services.Providers.Systemd`

Avoid generic namespaces such as `Helpers`, `Common`, or `Utils`. Place behavior with the responsibility that owns it.