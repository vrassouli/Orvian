# Roadmap

This file is the milestone summary. Executable acceptance criteria live in `docs/backlog/` and product boundaries live in `docs/product/mvp-scope.md`.

## Sprint 0 — Executable Foundation

Goal: prove the complete architecture with a runnable Avalonia shell, dynamically loaded sample plugin, fake transport, controlled command pipeline, in-memory audit, and architecture tests.

Completed foundation:

- [x] Repository and solution structure
- [x] Shared .NET build configuration
- [x] Initial plugin, command, audit, discovery, and secret contracts
- [x] Sample plugin
- [x] Initial tests and CI
- [x] Product, architecture, security, UX, SDK, ADR, and agent specifications

Remaining implementation:

- [ ] Avalonia application shell and composition root
- [ ] UI contribution abstractions and registries
- [ ] Plugin discovery/lifecycle prototype
- [ ] In-memory operation/command pipeline and fake transport
- [ ] In-memory audit sink and activity read model
- [ ] Security primitives and redaction prototype
- [ ] Architecture tests and expanded CI gates

Detailed plan: `docs/backlog/sprint-0.md`.

## Sprint 1 — Host Profiles, Connection, and Discovery

- SQLite persistence and migrations
- Host inventory
- Platform secret-store implementations
- SSH.NET transport and connection state machine
- Mandatory host-key verification
- Password/private-key authentication
- OS/fact/capability discovery
- Host Overview plugin

## Sprint 2 — Secure Execution and Auditing

- Production command pipeline
- POSIX argument encoding
- Timeout, cancellation, output bounds, and failure taxonomy
- sudo/doas privilege workflow with session-only credentials
- Persistent operation/command audit
- Risk confirmations
- Activity and audit UI

## Sprint 3 — First Operational Plugins

- Date & Time
- Services
- Users & Groups (read-only)
- Package Information (read-only)

Detailed Sprint 1–3 plan: `docs/backlog/sprints-1-to-3.md`.

## Post-MVP directions

These are not approved MVP scope:

- Docker and Podman feature plugins
- Firewall and network providers
- Nginx and database administration
- Additional providers for FreeBSD, macOS, OpenWrt, MikroTik, Proxmox, Cisco, and Juniper
- Plugin packaging, signing, registry, marketplace, and updates
- Fleet operations, scheduling, workflows, and desired-state automation
- Team/cloud synchronization and centralized policy
- Enterprise/tamper-evident audit export
- AI-assisted operation composition through the same security pipeline
- Additional management protocols

Every post-MVP item requires a task/specification and may require an ADR/security review before implementation.