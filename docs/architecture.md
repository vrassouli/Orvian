# Architecture

## Product boundary

Orvian is an agentless, cross-platform system administration platform. The desktop application connects to managed targets over SSH and composes capabilities through plugins.

## Layering

```text
GUI
 ↓
Feature
 ↓
Capability
 ↓
Provider
 ↓
SSH
```

Features must depend on capabilities, never directly on distribution names.

Bad:

```csharp
if (distribution == "Ubuntu")
```

Good:

```csharp
if (host.Capabilities.Contains("init.systemd"))
```

## Core responsibilities

The core owns:

- Application shell and navigation composition
- Plugin lifecycle
- SSH connections
- Structured command execution
- Privilege escalation
- Secret storage
- Audit and diagnostic logging
- Permission policy
- Notifications and dialogs

## Plugin responsibilities

Plugins own:

- Views and view models
- Business logic
- Capability requirements and contributions
- Command requests
- Parsing command output
- Navigation, search, commands, and settings contributions

Plugins never receive raw passwords, raw secret-store values, or direct SSH session access.

## Command execution invariant

Every remote command must pass through `ICommandExecutor`.

A plugin submits an executable and a structured argument collection. It must not build shell commands from untrusted input. The command pipeline is responsible for validation, escaping, privilege escalation, audit records, output limits, redaction, cancellation, and transport.

## Audit invariant

Every attempted remote command receives an audit record before execution and a completion update afterwards. This includes discovery and background commands, though diagnostic and user-facing audit views may present them differently.

Secrets are never stored in logs. Sensitive arguments are redacted and sensitive standard input is not retained.

## Plugin UI contributions

Plugins register metadata with the shell. They do not mutate shell controls directly. This allows the shell to enforce consistent navigation, permissions, accessibility, search, and lifecycle behavior.
