# Sprint 0: Executable Foundation

## Goal

Produce a runnable desktop skeleton that proves project boundaries, plugin composition, operation execution, audit ordering, persistence abstractions, and testability without implementing production host management.

## Exit criteria

- Avalonia application starts on supported developer platform.
- Shell renders host placeholder, navigation, content, activity, and settings regions.
- Sample plugin is discovered from controlled directory and contributes one page.
- In-memory operation/command pipeline enforces validation, permission, audit-start-before-execute, cancellation, and structured result.
- Fake transport executes deterministic read-only commands.
- In-memory audit sink records start/completion and failures.
- Plugin registration rollback and quarantine work.
- Architecture tests enforce forbidden dependencies.
- CI builds/tests Release successfully.

## Tasks

### S0-01 Create application and shell projects

Scope:

- Add `Orvian.App`, `Orvian.Shell`, `Orvian.UI.Abstractions`, and `Orvian.Application` as needed.
- Create composition root and minimal main window.
- Implement shell regions and common state placeholders without remote features.

Acceptance:

- App launches and closes cleanly.
- No network/database work on UI thread.
- Shell does not reference sample plugin directly.
- Basic keyboard navigation works.

Tests:

- Shell view-model state tests.
- Startup composition smoke test where practical.

### S0-02 Define contribution registries

Scope:

- Navigation, command, and settings contribution descriptors/builders.
- Atomic registration and uniqueness validation.

Acceptance:

- Duplicate routes/IDs reject the plugin registration.
- Core routes cannot be shadowed.
- Contributions are immutable after commit.

### S0-03 Implement local plugin discovery and lifecycle prototype

Scope:

- Manifest parsing, validation, compatibility, load context, enable state abstraction, quarantine, configure, dispose.

Acceptance:

- Valid sample plugin loads.
- Invalid/duplicate/incompatible plugin is isolated with reason.
- Failed registration commits no contributions.
- No root service provider or transport is exposed.

Tests:

- Fixture plugins for all lifecycle failure modes.

### S0-04 Implement in-memory operation/command pipeline

Scope:

- Operation coordinator, request validation, permission policy, fake transport, timeout/cancellation, output limits, result classification.

Acceptance:

- Transport cannot run before validation and audit start.
- Structured arguments remain distinct.
- Cancellation and timeout are distinguishable.
- Mutation fails closed when audit start fails.

### S0-05 Implement in-memory audit sink and activity read model

Acceptance:

- Start and completion transitions correlate by IDs.
- User and diagnostic sources can be filtered.
- Output metadata includes truncation/redaction mode.
- Shell activity region displays operation status using safe read models.

### S0-06 Establish security primitives

Scope:

- Permission identifiers/policy interfaces.
- Redaction descriptors and basic structural redactor.
- Secret-store abstraction and fake implementation for tests.
- Confirmation/risk models.

Acceptance:

- Sensitive values never appear in serialized audit fixtures.
- Plugins cannot retrieve arbitrary secrets.

### S0-07 Architecture and CI gates

Scope:

- Architecture test project.
- Dependency rules from `project-boundaries.md`.
- CI restore/build/test and formatting/analyzer baseline.

Acceptance:

- A deliberately forbidden test fixture fails architecture tests.
- Release CI is green.

## Out of scope

- SSH.NET transport.
- Real host profiles or SQLite.
- Real discovery.
- Privilege prompting.
- Production plugins.

## Sprint completion review

Before closing Sprint 0, demonstrate one end-to-end fake operation initiated from a dynamically loaded sample-plugin page, executed through the pipeline, and displayed in activity/audit without any plugin reference to transport or secrets.