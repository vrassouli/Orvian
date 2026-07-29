# Product Vision

## Mission

Orvian is a modern, cross-platform, agentless infrastructure administration desktop application. It gives infrastructure operators one consistent graphical workspace for discovering, observing, and safely administering heterogeneous remote systems over trusted management protocols, starting with SSH.

Orvian is not merely an SSH terminal. It translates infrastructure operations into understandable, auditable, permission-aware workflows while preserving access to low-level details when needed.

## Initial target systems

The first supported host families are:

- Linux distributions with POSIX-compatible shells.
- FreeBSD.
- macOS remote hosts.

Later providers may support:

- MikroTik RouterOS.
- OpenWrt.
- Proxmox VE.
- Cisco and Juniper network devices.
- Other SSH-manageable operating systems and appliances.

Future support does not imply that all targets share the same commands. Features depend on abstract capabilities and provider implementations.

## Primary users

- System administrators managing multiple servers.
- DevOps and platform engineers who need a safer operational workspace.
- Small and medium organizations without a large centralized management platform.
- Consultants managing customer infrastructure from one workstation.
- Advanced developers who administer development and staging systems.

## Product promise

A user should be able to:

1. Define or import a host connection.
2. Verify the host identity and authenticate securely.
3. Let Orvian discover facts and capabilities.
4. See only features supported by that host.
5. inspect the commands an operation will execute when appropriate.
6. Approve privileged or destructive actions.
7. Execute the operation through a controlled command pipeline.
8. Review the result and a complete audit trail.

## Core product principles

### Agentless by default

The initial product must not require installing an Orvian agent on managed hosts. It uses existing management interfaces, initially SSH and standard operating-system tools.

### Capability-based behavior

Features target capabilities such as `init.systemd`, `container.docker`, or `time.timedatectl`. They do not branch directly on distribution names except inside discovery or provider selection where distribution facts are legitimate evidence.

### Safe by construction

Plugins cannot access SSH sessions, passwords, or raw secrets. Remote execution, privilege escalation, logging, output limits, and cancellation are core services.

### Transparent operations

Users should understand what Orvian is doing. User-triggered operations expose purpose, target, privilege, progress, result, and audit history. The UI must distinguish discovery/background activity from intentional administrative changes.

### Extensible without weakening the core

Plugins contribute features and UI metadata through stable contracts. Extension must not bypass security, audit, navigation, or lifecycle policies.

### Local-first

The initial application stores host profiles, preferences, plugin state, and audit data locally. Cloud synchronization, teams, and centralized policy are later capabilities and must not be prerequisites for the desktop application.

### Cross-platform desktop quality

The application should feel native and usable on Windows, macOS, and Linux while sharing one Avalonia codebase. Accessibility, keyboard navigation, clear errors, and responsive long-running operations are requirements, not polish added later.

## Non-goals for the first release

- Replacing configuration-management systems such as Ansible, Puppet, or Salt.
- Running arbitrary unattended automation fleets.
- Acting as a general-purpose terminal emulator.
- Providing full monitoring, alerting, or time-series observability.
- Installing a mandatory server-side agent.
- Managing Windows hosts through SSH in the initial MVP.
- Multi-user cloud collaboration or a hosted control plane.
- Executing AI-generated commands without the same validation, permission, confirmation, and audit pipeline as human actions.

## Long-term direction

Orvian may evolve into an extensible infrastructure workspace with a plugin registry, reusable operations, scheduling, team policy, enterprise audit export, AI-assisted operation composition, and additional protocols. These extensions must preserve the core invariants defined in the architecture and security documentation.