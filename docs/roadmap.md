# Roadmap

This file is the milestone summary. Executable acceptance criteria live in `docs/backlog/` and product boundaries live in `docs/product/mvp-scope.md`.

## Platform delivery order

Orvian will be delivered in this order:

1. macOS
2. Windows
3. Linux
4. Mobile

macOS is the first implementation and validation platform. Cross-platform boundaries must still be preserved from the beginning so later Windows, Linux, and mobile presentation work does not require rewriting core behavior.

During development, GitHub Actions may publish unsigned macOS artifacts for internal testing. Paid Apple Developer Program membership, Developer ID signing, notarization, and production DMG release automation are deferred until the application is functionally complete and ready for public beta or stable distribution.

Distribution policy: `docs/development/macos-distribution.md`.

## Sprint 0 — Executable Foundation

Goal: prove the complete architecture with a runnable Avalonia shell, dynamically loaded sample plugin, fake transport, controlled command pipeline, in-memory audit, and architecture tests.

Completed foundation:

- [x] Repository and solution structure
- [x] Shared .NET build configuration
- [x] Initial plugin, command, audit, discovery, and secret contracts
- [x] Sample plugin
- [x] Initial tests and CI
- [x] Product, architecture, security, UX, SDK, ADR, and agent specifications

Completed implementation:

- [x] Avalonia application shell and composition root
- [x] UI contribution abstractions and registries
- [x] Plugin discovery/lifecycle prototype
- [x] In-memory operation/command pipeline and fake transport
- [x] In-memory audit sink and activity read model
- [x] Security primitives and redaction prototype
- [x] Architecture tests and expanded CI gates

Detailed plan: `docs/backlog/sprint-0.md`.

## Sprint 1 — Host Profiles, Connection, and Discovery

- [x] SQLite persistence and migrations
- [x] Host inventory
- [x] Platform secret-store implementations (Windows Credential Manager,
  macOS Keychain, and Linux Secret Service)
- [x] SSH.NET transport and connection state machine
- [x] Mandatory host-key verification
- [x] Password/private-key authentication
- [x] OS/fact/capability discovery with Linux/systemd, Linux/OpenRC,
  FreeBSD/rc.d, and macOS/launchd fixtures
- [x] Host Overview plugin

## Sprint 2 — Secure Execution and Auditing

- [x] Production command pipeline foundation
- [x] POSIX argument encoding
- [x] Timeout, cancellation, output bounds, and failure taxonomy
- [x] sudo/doas privilege workflow with session-only credentials
- [x] Persistent operation/command audit
- [x] Risk confirmations
- [x] Initial activity and audit UI
- [x] Typed application settings, conservative recovery, and audited retention cleanup
- [x] Bounded redaction-first local diagnostics and startup/crash correlation
- [x] Core-resolved authenticated-user identity in durable audit records
- [x] Bounded redaction-safe Activity search with retention-synchronized indexes
- [x] Explicit remembered-credential retain/replace/remove lifecycle in host editing
- [x] Clear session-only host credential flow when native secure storage is unavailable
- [x] Deterministic merged Activity paging with bounded look-ahead and load-more UI
- [x] Opt-in live Linux SSH/discovery/provider compatibility gate
- [x] Real multi-line tool-version discovery compatibility

## Sprint 3 — First Operational Plugins

- Date & Time (systemd/POSIX reads and confirmed systemd timezone/date-time mutations complete)
- Services (systemd inventory and confirmed start/stop/restart/enable/disable complete)
- Users & Groups (getent-based read-only inventory complete)
- Package Information (apt/rpm, pacman, zypper, apk, pkg, and brew reads complete)

Detailed Sprint 1–3 plan: `docs/backlog/sprints-1-to-3.md`.

## Release-readiness milestone — macOS public distribution

This milestone begins only after the application is functionally complete enough for public beta or stable distribution.

- Apple Developer Program enrollment
- Developer ID Application certificate
- Hardened Runtime and entitlement review
- GitHub Actions signing-secret setup
- Code signing and nested-component verification
- Apple notarization and stapling
- Signed DMG generation
- Checksums and GitHub Release publication
- Clean-machine installation validation

Until this milestone is explicitly activated, ordinary CI must not depend on paid Apple services or release credentials.

## Post-MVP directions

These are not approved MVP scope:

- Docker and Podman feature plugins
- Firewall providers and additional network providers, including rollback-safe Netplan mutation
- Nginx and database administration
- Additional providers for FreeBSD, macOS, OpenWrt, MikroTik, Proxmox, Cisco, and Juniper
- Plugin packaging, signing, registry, marketplace, and updates
- Fleet operations, scheduling, workflows, and desired-state automation
- Team/cloud synchronization and centralized policy
- Enterprise/tamper-evident audit export
- AI-assisted operation composition through the same security pipeline
- Additional management protocols

Every post-MVP item requires a task/specification and may require an ADR/security review before implementation.
