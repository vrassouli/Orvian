# Testing Strategy and Definition of Done

## Test layers

### Unit tests

Cover value objects, validation, state transitions, redaction, permission policy, provider selection, parsers, result classification, and application orchestration with fakes.

### Architecture tests

Verify project/package dependency rules, forbidden plugin references, public contract neutrality, and absence of SSH.NET/SQLite dependencies in plugin/core contract assemblies.

### Integration tests

Use controllable fake transport, fake secret store, temporary SQLite database, fake clock, and plugin fixtures. Exercise command/audit ordering, connection/discovery flows, plugin lifecycle, migrations, and cancellation.

### Remote compatibility tests

Use disposable containers/VMs or explicitly managed test hosts for supported providers. Never use production hosts or real credentials in CI. Test matrices should eventually include representative systemd Linux, OpenRC Linux, FreeBSD, and macOS where runner access permits.

### UI tests

Test shell/view-model behavior, navigation contributions, common page states, focus/keyboard paths, dialogs, and error rendering. Keep core behavior outside view code so most tests remain fast.

## Required quality cases

Security-sensitive work includes:

- Negative permission tests.
- Secret/redaction serialization tests.
- Host-key unknown/changed tests.
- Hostile argument/quoting tests.
- Malformed/truncated remote output tests.
- Timeout/cancellation/connection-loss tests.
- Audit storage failure tests.

Persistence work includes migration, rollback, concurrency, and recovery tests.

Plugin work includes manifest, compatibility, registration rollback, disposal, permission, capability, provider, and parser tests.

## Test conventions

- Tests are deterministic and do not depend on internet access.
- No sleeps for synchronization when a controllable clock/event can be used.
- No real credentials or identifying infrastructure data in fixtures.
- Test names describe behavior and condition.
- A failed test should explain the broken invariant.
- Snapshot/golden tests must be reviewed for accidental secrets and unstable values.

## CI gates

At minimum:

```text
dotnet restore Orvian.slnx
dotnet build Orvian.slnx --configuration Release --no-restore
dotnet test Orvian.slnx --configuration Release --no-build
```

As projects mature, CI adds formatting/analyzers, architecture tests, package validation, vulnerability/dependency review, and multi-platform build checks.

Warnings remain errors. Suppressions require a narrow justification in code or project configuration; global suppression to make CI green is prohibited.

## Definition of Done

A change is done only when all applicable statements are true:

### Behavior

- Acceptance criteria are fully satisfied.
- In-scope failure, loading, empty, disconnected, unsupported, and cancellation states are handled.
- No excluded scope was added implicitly.

### Architecture

- Correct project/layer owns the behavior.
- Dependency and plugin boundaries remain intact.
- New public contracts are minimal, documented, and compatibility-aware.
- Significant decisions have an ADR.

### Security

- Secrets cannot enter persistence, logs, audit, UI copy details, or exceptions.
- Remote execution uses the command pipeline.
- Permissions, confirmation, host identity, privilege, redaction, and audit policy are enforced by core services.
- Security-sensitive code has negative tests.

### Reliability

- Async work is cancellation-aware and bounded.
- No UI-thread blocking or unobserved background exceptions.
- Persistence and audit failures are not reported as success.
- Partial completion and truncation are visible.

### UX

- Common states and actionable errors are implemented.
- Keyboard/accessibility basics are preserved.
- User-facing strings are localizable.
- Destructive effects and target host are clear.

### Verification

- Relevant unit/integration/architecture/UI tests pass.
- Release build passes locally or in CI.
- Manual verification is documented when automation is impractical.
- No real credentials or environment-specific artifacts were committed.

### Documentation

- Governing docs, API comments, manifest schema, migrations, and examples are updated.
- PR explains security, architecture, data, and compatibility impact.
- Follow-ups are explicit issues, not hidden unfinished behavior.

Code that merely compiles is not complete.