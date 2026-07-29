# Roadmap

## Sprint 0 — Foundation

Goal: validate the architectural boundaries before implementing production administration features.

- [x] Repository and solution structure
- [x] Shared .NET build configuration
- [x] Plugin contracts
- [x] Structured command contracts
- [x] Audit contracts
- [x] Discovery models
- [x] Secret store abstraction
- [x] Sample plugin
- [x] Initial tests and CI
- [ ] Avalonia application shell
- [ ] Plugin discovery and loading
- [ ] In-memory command pipeline prototype
- [ ] In-memory audit sink
- [ ] Architecture decision records

## Sprint 1 — Connection and discovery

- Host profiles
- SSH authentication models
- Host-key verification
- Connection lifecycle
- OS and capability discovery
- Discovery diagnostic logs
- Initial host overview page

## Sprint 2 — Secure execution and auditing

- SSH.NET transport
- Privilege escalation workflow
- Session-only sudo credential handling
- Command redaction
- Output truncation
- SQLite audit persistence
- Activity and audit-log UI

## Sprint 3 — First operational plugins

- Date & Time
- Services
- Users and groups (read-only first)
- Package information

## Later milestones

- Docker and Podman
- Firewall providers
- Network configuration
- Nginx and databases
- Plugin SDK packaging and registry
- Team and enterprise audit capabilities
- AI-assisted operation composition
- Additional platforms and network devices
