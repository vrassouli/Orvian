# Command and Operation Pipeline

## Purpose

The command pipeline is the only legal path for remote execution. It converts an authorized operation intent into one or more audited transport calls while protecting secrets, enforcing policy, and returning structured results.

## Operation model

An operation represents one intention such as `Restart nginx`, `Change timezone`, or `Refresh discovery`.

Required operation fields:

- Stable operation ID.
- Plugin ID and plugin version.
- Invocation source.
- Host profile ID and connection ID.
- User-facing title and purpose.
- Risk level: informational, low, elevated, destructive.
- Required plugin permission.
- Requested privilege.
- Optional resource lock key.
- Started/completed timestamps and terminal status.
- Child command IDs.

An operation can succeed, partially succeed, fail, be denied, be cancelled, time out, or be interrupted. Multi-command plugins must define whether partial completion needs compensation or a clear manual-recovery message.

## Command request

A command request contains:

- Operation ID.
- Plugin identity supplied by trusted plugin context, not user input.
- Executable name or absolute path according to policy.
- Ordered structured arguments.
- Per-argument sensitivity metadata.
- Optional standard input descriptor; sensitive input is never retained.
- Required privilege.
- Required declared permission.
- Timeout within platform limits.
- Output logging mode and parser expectations.
- Invocation source inherited from the operation.
- Human-readable reason for privileged or exceptional execution.

Plugins do not choose host credentials, transport sessions, raw shell escaping, audit identifiers, or sudo password handling.

Sensitive standard input is reserved for core execution services. A plugin
cannot place stdin on its initial `CommandRequest`; the privilege preparer may
attach a disposable character buffer only after validation, permission checks,
and the audit-start write. The transport clears encoded byte buffers and the
executor disposes the prepared input in a `finally` path.

## Processing sequence

1. Validate required fields and operation state.
2. Resolve trusted plugin runtime identity and compare it with request ownership.
3. Confirm the plugin declared the requested permission.
4. Verify selected host and connection state.
5. Validate executable and argument constraints.
6. Determine risk, confirmation, and privilege policy.
7. Resolve the authenticated remote user from the core-owned host profile.
8. Create and persist the operation audit start record.
9. Obtain required core-owned confirmation.
10. Acquire a resource lock when required.
11. Create and persist each command audit start record.
12. Resolve a privilege provider without revealing credentials to the plugin.
13. Build a transport-safe invocation.
14. Execute with timeout and cancellation.
15. Capture bounded output and truncation metadata.
16. Classify transport, exit, cancellation, timeout, and privilege outcomes.
17. Persist command and operation audit completion in finally-style paths.
18. Return a structured result to the caller.
19. Release resource lock and connection lease.

No transport call may happen before the audit-start attempt and policy validation.
If the core cannot resolve a valid authenticated remote username, operation and
command execution fail closed before audit start and transport. Plugins do not
supply or override this identity.

## Argument safety

The default execution model uses one executable and an ordered argument collection. The transport implementation performs POSIX-safe argument quoting. Plugins may not pre-quote arguments.

User-controlled values must remain individual arguments. For example, a service name is one argument, not interpolated into `systemctl restart {name}`.

Pipes, redirects, command substitution, glob expansion, variable expansion, compound commands, and shell operators require an explicit shell-command request type. That request:

- Requires a stronger permission.
- Must provide a justification.
- Must use trusted templates with separately encoded values.
- Receives additional review and tests.
- Is never introduced merely because quoting is inconvenient.

## Executable policy

- Executable cannot be empty or contain NUL/newline characters.
- Provider implementations should use known executable names discovered on the host.
- Arbitrary executable paths from user input are prohibited unless the feature explicitly supports them with validation.
- Environment modification is denied by default and requires structured allow-listed variables.
- Working directory is optional, structured, validated, and defaults to the remote account home or transport default.

## Confirmation policy

Confirmation is core-owned and based on operation metadata, not plugin-controlled UI alone.

- Informational reads: no confirmation by default.
- Low-risk reversible mutations: concise confirmation may be configured.
- Elevated mutations: confirmation displays target, purpose, privilege, and expected effect.
- Destructive actions: explicit destructive confirmation; bulk scope must be obvious.
- Retries do not inherit confirmation indefinitely; core policy decides whether material parameters changed.

## Timeout and cancellation

- Every command has a finite timeout.
- Platform defaults apply when a plugin does not request a shorter valid timeout.
- Cancellation requests stop local waiting and request transport cancellation.
- If remote termination cannot be guaranteed, result status is `Interrupted` or `CancellationUncertain`, not falsely `Cancelled`.
- A timeout is distinct from nonzero exit and connection loss.

## Output handling

- Output is processed incrementally where possible.
- Defaults: 1 MiB stdout and 256 KiB stderr stored maximum.
- Metadata records original observed byte count when known and whether beginning, end, or both were retained.
- Invalid encoding is replaced safely and noted.
- ANSI/control sequences are sanitized for UI and logs.
- Parser receives the bounded raw logical output plus truncation metadata; it must not silently treat truncated data as complete.

Logging modes:

- `Full`: bounded content stored after redaction.
- `Redacted`: redaction rules applied; sensitive sections replaced.
- `MetadataOnly`: size, hash if policy allows, and truncation state only.
- `Disabled`: no content retained, but execution metadata remains audited.

## Result model

A command result includes:

- Command ID and operation ID.
- Terminal status.
- Exit code when available.
- Start, completion, and duration.
- Captured stdout/stderr and truncation metadata according to policy.
- Failure classification and safe message.
- Whether privilege escalation occurred.
- Connection interruption/cancellation certainty.

`IsSuccess` is true only for an allowed success status and acceptable exit code. Parsing failure is represented separately from command execution success.

## Audit failure behavior

Mutation commands fail closed if the mandatory audit start cannot be persisted. Read-only discovery may run in explicitly configured degraded mode only when a local diagnostic event can still be written; default behavior is also fail closed.

Failure to write completion must be surfaced as an application integrity problem. The in-memory operation result must not be rewritten as fully audited success.

## Retry behavior

- Automatic retry is allowed only for idempotent read operations and transient failures explicitly classified as retryable.
- Mutations are not automatically retried unless provider semantics prove idempotence and operation policy permits it.
- Each retry is a new command attempt under the same operation and has its own audit entry.
- Backoff is bounded and cancellation-aware.

## Testing requirements

Tests must cover validation, permission denial, audit ordering, sensitive argument redaction, quoting edge cases, timeout, cancellation uncertainty, output truncation, nonzero exit, connection loss, privilege denial, audit persistence failure, and retry rules.
