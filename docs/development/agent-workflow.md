# Agent Workflow

## Before changing code

Every agent must:

1. Read `/AGENTS.md` and `docs/README.md`.
2. Read the product vision, requirements, and MVP scope.
3. Read architecture/security/UX/SDK documents governing the task.
4. Inspect current code and tests; do not assume documentation has already been implemented.
5. Identify the exact project boundary that owns the change.
6. Restate acceptance criteria internally and list likely security/failure cases.
7. Check accepted ADRs and do not contradict them silently.

## Implementation rules

- Make the smallest coherent change that completely satisfies the task.
- Preserve dependency direction and do not bypass an abstraction to save time.
- Prefer explicit domain types and structured results over booleans/string errors.
- All asynchronous APIs accept/propagate cancellation when work can block.
- Never block with `.Result`, `.Wait()`, or synchronous network/database calls on the UI thread.
- Never add a plaintext secret fallback.
- Never execute a remote command outside `ICommandExecutor`/operation services.
- Never introduce shell interpolation for user or remote-derived input.
- Never make a public plugin contract change without updating SDK docs and compatibility tests.
- Do not implement excluded MVP functionality unless the issue explicitly changes scope.

## Handling ambiguity

An agent may make local implementation choices when they do not affect:

- Product behavior.
- Security policy.
- Public contracts.
- Persistence compatibility.
- Supported platforms.
- User-visible destructive behavior.

For material ambiguity, do not hide an assumption. Prefer an ADR proposal, a documented TODO linked to an issue, or a clearly stated PR question. Do not invent cloud services, credentials behavior, permissions, or plugin trust guarantees.

## Expected task structure

A good task contains:

- Context and user value.
- Desired outcome.
- In scope/out of scope.
- Governing document links.
- Acceptance criteria.
- Test requirements.
- Security/compatibility notes.

Use `docs/backlog/task-template.md` when creating work.

## Test-first risk analysis

Before implementation, enumerate at least:

- Happy path.
- Validation failure.
- Cancellation/timeout where relevant.
- Dependency/persistence/transport failure.
- Security denial or redaction case.
- Cross-platform/provider variation.
- Partial/truncated/malformed remote output for parsers.

Tests should target public behavior rather than private implementation details.

## PR expectations

A PR description includes:

- Problem and solution.
- Scope and non-scope.
- Governing docs/ADRs.
- Architectural impact.
- Security impact.
- Data migration impact.
- Tests executed and results.
- UI screenshots for visible changes.
- Remaining risks/follow-ups.

Do not claim tests were run when they were not. CI must be green before merge unless a repository owner explicitly accepts an infrastructure exception.

## Documentation synchronization

Update documentation in the same PR when changing:

- User-visible behavior.
- Public contracts or manifest schema.
- Permission/capability names.
- Persistence schema or retention.
- Security policy.
- Supported platforms/providers.
- Sprint acceptance criteria or scope.

## Review checklist

- Correct project owns the code.
- No forbidden references.
- No raw SSH/secret exposure.
- Audit ordering and fail-closed behavior preserved.
- Structured arguments and redaction correct.
- Cancellation/timeouts bounded.
- UI states and errors are accessible and safe.
- Tests include negative/security cases.
- Documentation and ADRs are current.

## Completion report

At the end of an autonomous task, report:

- Files/projects changed.
- Behavior implemented.
- Tests and build commands run.
- Known limitations.
- Any documentation/ADR decisions.
- Exact follow-up work that remains, without pretending it is complete.