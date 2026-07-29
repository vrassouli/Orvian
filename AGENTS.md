# AGENTS.md

## Mission

Build Orvian as a cross-platform, agentless infrastructure administration desktop application using .NET 10, Avalonia UI, MVVM, SSH.NET, SQLite, and a plugin-first capability/provider architecture.

Orvian is not a generic SSH terminal. It provides safe, understandable, auditable administration workflows while preserving target-specific behavior behind providers.

## Required reading

Before changing code, read:

1. `docs/README.md`
2. `docs/product/vision.md`
3. `docs/product/product-requirements.md`
4. `docs/product/mvp-scope.md`
5. `docs/architecture/overview.md`
6. `docs/architecture/project-boundaries.md`
7. The task-specific architecture, security, UX, SDK, ADR, and backlog documents
8. `docs/development/agent-workflow.md`
9. `docs/development/testing-and-definition-of-done.md`

Documentation precedence and ambiguity rules are defined in `docs/README.md`. Do not silently invent material product, security, persistence, or public-contract behavior.

## Non-negotiable invariants

1. Plugins never receive SSH.NET sessions, transport implementations, passwords, private keys, passphrases, privilege credentials, raw secret-store access, SQLite contexts, or the root service provider.
2. Every remote command—including discovery, background work, and future AI activity—passes through the core operation/command pipeline.
3. Every attempted remote command receives an audit-start attempt before transport execution and a completion/failure update afterwards.
4. Mutation commands fail closed when mandatory audit-start persistence is unavailable.
5. Commands use an executable plus structured arguments. User or remote-derived values are never interpolated into shell text.
6. Shell syntax is an explicit restricted exception requiring stronger permission, justification, encoding, and tests.
7. Features depend on semantic capabilities and provider selection, not distribution checks scattered through feature code.
8. Host-key verification is mandatory and cannot be bypassed by plugins.
9. Secrets never appear in SQLite, logs, audit records, UI copy-details, exceptions, test fixtures, or command arguments when a safer channel exists.
10. Security policy, confirmation, privilege, permission, redaction, timeout, output limits, and retry are enforced by core services at execution time.
11. Plugins register immutable metadata/contributions; they do not mutate shell controls or registries directly.
12. Core projects never reference concrete feature plugins.
13. Remote output is untrusted, bounded, sanitized, and defensively parsed.
14. UI/network/database work is asynchronous, cancellation-aware, bounded, and never blocks the UI thread.
15. No excluded MVP functionality is added without an explicit issue or accepted decision updating scope.

## Technology and boundaries

- Target .NET 10.
- Avalonia UI with MVVM.
- SSH.NET is isolated inside `Orvian.Ssh`; no SSH.NET type crosses that project boundary.
- SQLite is isolated inside `Orvian.Persistence`.
- Platform secure stores implement `ISecretStore`; no plaintext fallback.
- Nullable reference types and warnings-as-errors remain enabled.
- Public plugin contracts remain platform-neutral and compatibility-aware.

Follow `docs/architecture/project-boundaries.md` for project ownership and forbidden references. Avoid generic `Helpers`, `Common`, or `Utils` dumping grounds.

## Working method

- Inspect current implementation and tests before editing; documentation defines intended behavior but may not yet be implemented.
- Make the smallest coherent change that fully satisfies the task.
- Add or update tests for happy path, failure, cancellation/timeout, security denial/redaction, malformed/truncated output, persistence/transport failure, and provider variation as applicable.
- Significant architectural decisions require an ADR.
- Public contract, manifest, capability, permission, schema, security, or user-behavior changes require matching documentation updates.
- Keep commits focused and PR descriptions honest about tests, limitations, security, migration, and compatibility impact.

## Repository layout

- `src/`: shell, application, domain/contracts, and infrastructure projects.
- `plugins/`: first-party plugins using public SDK boundaries.
- `tests/`: unit, architecture, integration, and UI tests.
- `docs/`: authoritative product, architecture, security, UX, SDK, development, ADR, and backlog specifications.

## Completion standard

A change is complete only when the applicable Definition of Done in `docs/development/testing-and-definition-of-done.md` is satisfied. Compiling code alone is not complete.