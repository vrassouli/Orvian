# Sprints 1 to 3

# Sprint 1: Host Profiles, SSH Connection, and Discovery

## Goal

Connect securely to real supported hosts, verify identity, authenticate, discover capabilities, and present a trustworthy host overview.

## Deliverables

### Host inventory and persistence

- SQLite database, migrations, repository contracts/implementations.
- Host create/edit/delete/disable and search.
- Secret references with platform backends and session-only fallback behavior.
- Validation and compensation for cross-store failures.

### SSH transport and connection manager

- SSH.NET hidden behind transport interfaces.
- Password and private-key authentication.
- Host-key unknown/matching/changed flow.
- Connection state machine, cancellation, bounded reconnect, disconnect/shutdown.
- Structured safe failure mapping.

### Discovery

- Independent probes for OS/kernel/architecture/shell/user/privilege/init/package/date tools.
- Normalized facts, capabilities, immutable snapshots, partial completion.
- Provider selection diagnostics and manual refresh.

### Host overview plugin

- Current/cached host facts, freshness, capabilities, connection state.
- Connect/disconnect/refresh actions.
- Common unsupported/error/loading states.

## Exit acceptance

- Clean install can securely store or session-use a credential.
- First connection requires explicit host-key trust.
- Changed key blocks connection by default.
- Representative Linux host discovery succeeds partially or fully.
- Disconnected cached facts are visibly stale.
- Every discovery command is diagnostic-audited.
- No plugin references SSH.NET or raw secret store.

## Required tests

- SQLite migrations/transactions.
- Secret compensation and unavailable backend.
- Connection state transitions and interruption.
- Unknown/matching/changed host key.
- Authentication variants and cancellation.
- Discovery fixtures for Linux/systemd, OpenRC, FreeBSD/macOS parsing where implementation exists, and partial failures.

# Sprint 2: Privilege, Production Command Pipeline, and Persistent Audit

## Goal

Safely execute real read and mutation operations with privilege escalation, redaction, bounded output, and durable activity/audit history.

## Deliverables

### Production execution

- SSH command transport adapter.
- POSIX argument encoder with adversarial tests.
- Timeout, cancellation certainty, connection loss, bounded output, control-sequence sanitation.
- Operation locks and idempotent-read retry policy.

### Privilege

- Detect already-root, sudo, and doas providers as supported.
- Core-owned secure prompt and session-only cache.
- Non-echoing stdin path.
- Clear denial/failure results.

### Audit

- SQLite operation/command schema, start/completion writes, paging/filter queries.
- Output modes, structural redaction, truncation metadata.
- Activity and audit UI.
- Fail-closed mutation behavior on audit-start failure.

### Security confirmations

- Informational/low/elevated/destructive risk model.
- Core-owned confirmation dialog with target, effect, privilege, and redacted details.

## Exit acceptance

- A controlled privileged mutation can be confirmed, elevated, executed, and fully audited.
- Secret values cannot be found in audit/log serialization tests.
- Timeout, cancellation uncertainty, nonzero exit, privilege denial, and persistence failure display distinct outcomes.
- Audit list remains paged and does not eager-load output.

# Sprint 3: First Operational Plugins

## Goal

Prove capability/provider portability through useful administration features built exclusively on the public plugin SDK.

## Date & Time plugin

Read:

- Remote time.
- Timezone.
- Synchronization/NTP state when supported.

Mutate:

- Timezone change.
- Time adjustment only where policy/provider is well defined; prefer synchronization configuration over arbitrary manual time changes.

Providers may include systemd/timedatectl, POSIX date plus platform-specific tools, FreeBSD, and macOS implementations as tested.

Acceptance:

- Input timezone is selected/validated, never interpolated.
- Current state refreshes after mutation.
- Privilege and risk are correct.
- Unsupported write mode remains read-only with explanation.

## Services plugin

Read:

- List services with normalized name/display/status/start mode where available.
- Service details and recent safe status text.

Mutate:

- Start, stop, restart, enable, disable.

Initial provider: systemd. Additional providers only with fixtures and explicit scope.

Acceptance:

- Service identifier is a structured validated argument.
- Stop/restart uses confirmation appropriate to risk.
- Result refreshes the affected service.
- Partial/ambiguous status parsing is visible.

## Users & Groups plugin

MVP behavior is read-only:

- List users/groups.
- Show identity, IDs, home, shell, and membership using safe commands/files.
- Do not read password hashes or secret files.

Mutation remains excluded until a separate reviewed task defines portability, validation, rollback, and security behavior.

## Package Information plugin

- Detect active package provider.
- Show selected installed package facts and versions using bounded/paged queries.
- Installation, upgrade, removal, and repository mutation remain out of scope.

## Sprint 3 exit acceptance

- At least Date & Time and Services work end-to-end on a representative Linux/systemd host.
- Plugins use only public SDK contracts.
- Architecture tests catch direct infrastructure references.
- Host capability changes update feature availability deterministically.
- All mutations have confirmation, privilege, output, and audit coverage.