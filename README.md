# Orvian

Orvian is a cross-platform, agentless system administration platform for managing Linux, FreeBSD, macOS, and future infrastructure targets over SSH.

> Project status: early foundation / Sprint 0

## Vision

Orvian aims to provide a Windows-like administration experience without requiring an agent on managed systems.

Core principles:

- Agentless management over SSH
- Capability-based behavior instead of distribution-specific branching
- Plugin-first architecture
- Centralized command execution, privilege handling, security, and auditing
- Cross-platform desktop UI built with Avalonia
- Safe extensibility for future targets such as MikroTik, OpenWrt, Proxmox, Cisco, and Juniper

## Architecture

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

Plugins contribute UI, discovery, capabilities, and business logic. They never receive raw SSH sessions, passwords, or secrets and never execute commands directly.

All remote commands flow through the core command pipeline:

```text
Plugin
 ↓
Command Execution Service
 ↓
Permission / Privilege Policy
 ↓
Audit
 ↓
SSH
```

## Technology

- .NET 10
- Avalonia UI
- MVVM
- SSH.NET
- Microsoft.Extensions.DependencyInjection
- SQLite for local persistence
- Native operating-system secret stores

## Repository layout

```text
src/
  Orvian.App
  Orvian.Core
  Orvian.Plugin.Abstractions
  Orvian.Ssh
  Orvian.Security
  Orvian.Auditing
  Orvian.Discovery
plugins/
  Orvian.Plugins.Sample
tests/
docs/
```

## Current goals

Sprint 0 establishes the architectural contracts and development infrastructure. It intentionally avoids implementing production system-management features until the foundation is validated.

See [docs/roadmap.md](docs/roadmap.md) and [docs/architecture.md](docs/architecture.md).

## Building

Install a .NET 10 SDK, then run:

```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

## License

A license has not yet been selected. Until one is added, all rights are reserved.
