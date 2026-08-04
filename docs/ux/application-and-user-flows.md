# Application UX and User Flows

## Information architecture

The main window contains:

- Top/title area: current host identity, connection state, global search/command access, activity indicator.
- Left host/navigation region: host selector and feature navigation.
- Main content region: selected feature page.
- Activity access: running/recent operations and audit details.
- Settings and plugin management entry.
- Notification surface for transient, actionable feedback.

The shell must work with no hosts, disconnected hosts, partially discovered hosts, unsupported features, and plugin failures.

The host search field accepts ordinary text plus composable structured filters:
`tag:value`, `os:linux`, `capability:init.systemd`, `state:ready`, and
`enabled:true|false`. Repeating tag or capability filters requires all supplied
values. OS and capability filters use only the latest persisted discovery
snapshot; connection state uses the live application snapshot. Invalid filter
values show a safe actionable message and do not issue an unbounded query.

## First-run flow

1. Show a concise product introduction and local-first/security expectations.
2. Present `Add host` as the primary action.
3. Do not require account creation or cloud connectivity.
4. Explain secure secret storage when the user chooses to remember a credential.

## Add/edit host

Fields:

- Display name.
- Hostname or IP.
- Port.
- Username.
- Authentication method.
- Password or private-key selection through secure controls.
- Remember credential choice when backend is available.
- Optional tags and notes.
- Connection timeout from 1 to 120 seconds.
- Additional transient network retry attempts from 0 to 5.

Validation is inline and does not erase entered data. `Test connection` uses the same verification/authentication services but does not silently persist trust or secrets without explicit choices.

If the native secure-store backend is unavailable, the remember control is
disabled and the form explains that the entered credential lasts only for the
current application session. The user can still create and connect to the host;
no plaintext or substitute persistent store is created.

Host inventory actions remain available for disabled profiles. Disabling a host
disconnects it before committing the disabled state and clears SSH and privilege
credentials held only for the current session. Re-enabling does not reconnect
automatically. Editing supports tags and notes. Duplicating creates a new stable
profile identity and copies non-secret profile fields only; credential
references, host-key trust, discovery snapshots, and session credentials are
never inherited.

Connection preferences are editable per host and use the same domain validation
as persisted profiles. Editing without changing them preserves the current
values, and duplication copies these non-secret preferences. “Additional retry
attempts” is labeled explicitly so zero means one initial attempt and no retry.
When a remembered credential exists, editing states that fact without showing
its value and offers explicit keep, replace, or remove behavior. A user may
remove the remembered credential while supplying a session-only replacement;
leaving both credential actions unchecked preserves the existing secure-store
reference.

## Connection flow

Connection progress uses meaningful stages: resolving, connecting, verifying identity, authenticating, and discovering. The user can cancel.

While establishment is active, the host’s Connect button becomes Cancel instead
of starting or queueing another connection. Cancellation propagates through
DNS/SSH transport and any pending retry delay, produces the non-error
`Cancelled` state, and restores the Connect action. Closing the main window
also requests cancellation of the active establishment attempt.

Unknown host key dialog displays:

- Requested host and resolved address when available.
- Key algorithm.
- SHA-256 fingerprint.
- Explanation that first trust should be verified through an independent channel for sensitive systems.
- `Trust and continue` and `Cancel` actions.

Changed host key dialog is visually severe, defaults to cancel, shows previous and new fingerprints, and requires an explicit replace-trust action. It must not be a routine one-click warning.

Authentication errors preserve the profile and offer retry/edit options. Network and identity errors are distinct.

Transient network establishment failures may retry according to the profile's
bounded reconnect preference. Authentication and host-identity outcomes never
retry automatically. Cancellation stops both an active attempt and any pending
retry delay. If the budget is exhausted, the error states the total number of
attempts; there is no hidden or infinite reconnect loop.

## Host overview

Show:

- Display name, address, verified fingerprint summary.
- Connected/disconnected/interrupted state.
- OS family/distribution/version, architecture, kernel, hostname, uptime.
- Discovery freshness and partial-failure indicator.
- Available privilege provider.
- Capability summary and active provider explanations.
- Actions: connect/disconnect, refresh discovery, enable/disable, edit,
  duplicate, delete, and view activity.

Cached information shown while disconnected is clearly marked stale.

## Feature pages

Every feature page supports common states:

- No host selected.
- Disconnected.
- Connecting/discovering.
- Unsupported with reason.
- Read-only due to capability or permission.
- Loading/refreshing without clearing usable prior data unnecessarily.
- Empty.
- Error with safe details and retry.
- Ready.

Feature pages do not execute mutations on selection. Mutations begin only from explicit actions.

The Date & Time feature presents timezone and manual date/time changes as
separate tabs rather than a mutation selector. Manual date/time input uses a
calendar-backed date picker and a time picker when the desktop toolkit provides
them. The selected values are serialized to the canonical remote-local format
`YYYY-MM-DD HH:mm:ss`; both actions remain privileged, confirmed, audited, and
refresh the displayed state after success.

## Mutation flow

1. User initiates action.
2. Plugin/application validates current model and builds operation intent.
3. Core shows confirmation according to risk.
4. If elevation is needed, core explains why and requests credential securely.
5. Activity surface shows queued/running progress.
6. Success updates view state or refreshes affected data.
7. Failure shows classified guidance and links to operation details.

Destructive buttons use clear verbs such as `Stop service` rather than generic `OK`.

## Activity and audit

Activity list shows operation title, host, source, plugin, start time, duration, status, and progress. Expanding an operation shows command attempts and redacted details.

Audit filters:

- Date range.
- Host.
- Plugin/feature.
- Invocation source.
- Status.
- Privilege/risk.
- Safe metadata search.

Command details include executable, redacted arguments, exit code, timing, truncation state, output according to policy, and safe failure classification. Diagnostic discovery commands are hidden by default from the user-action view but available through a diagnostic filter.

Operation and command rows show the authenticated remote username captured at
execution time. The command detail view repeats it alongside host, connection,
plugin, and command identity; it is not inferred later from a possibly edited
display label.

The MVP Activity window implements host, plugin, start-date range, invocation
source, status, privilege, risk, and bounded safe-metadata search. Search
requires every whitespace-separated term to match indexed operation, command,
or host-trust metadata; it never searches retained command output or raw
secrets. Operation intentions remain the default rows.
Enabling discovery diagnostics exposes child command attempts; selecting a
command enables its detail view. Retained output is labeled by the effective
policy and is never fetched by list queries. Activity initially loads 50 rows;
`Load more` advances independent operation, command, and host-trust cursors.
Applying or clearing filters starts a fresh timestamp-bounded sequence, while a
failed later page preserves rows already shown.

## Plugin management

Show installed plugin name, publisher, version, state, compatibility,
permissions, and safe errors. The MVP Settings window exposes appearance,
locale-ready formatting, new-host connection defaults, output/audit retention,
and the conservative clear-session-credentials-on-disconnect preference.
Retention cleanup is an explicit bounded audited action; output retention cannot
exceed metadata retention. Host-key verification and mutation auditing are
shown as mandatory and cannot be disabled. The same window opens plugin
inventory and supports immediate enable/disable for discovered compatible
plugins; navigation contributions refresh after a successful change. A failed
state change shows a safe generic error and retains the prior durable state. Do
not imply third-party plugins are sandboxed.

## Error presentation

- User message: concise, specific, and actionable.
- Technical details: expandable, redacted, includes correlation ID.
- Raw stack traces are not shown in normal UI.
- Copy details uses the same redacted representation.
- Persistent failures should not produce repeated notification spam.

Settings provides an Application diagnostics viewer for bounded local
structured events. It shows timestamp, severity, category, safe event code,
mapped message, correlation ID, and sanitized properties. These events remain
visually and structurally separate from remote command/operation audit data.
Startup and plugin-load failures include the same correlation ID in their
user-facing message and diagnostic record.

## Keyboard and accessibility

- Full navigation and dialogs are keyboard operable.
- Logical focus after navigation, dialog close, and operation completion.
- Accessible names for icons and status controls.
- Status uses text/icon in addition to color.
- Destructive confirmations do not rely on color alone.

## Responsive behavior

The app is desktop-first. At narrower widths, host/navigation regions may collapse into drawers, but host identity, connection state, primary content, and operation access remain reachable.

## UI consistency rules for agents

- Reuse shell-owned dialogs, notifications, status, empty/error components, and activity UI.
- Plugins provide view models/descriptors, not custom global chrome.
- Never expose a raw secret in a text control after submission.
- Never claim success before persistence/audit completion is confirmed.
- Never hide partial failure in a multi-command operation.
- Avoid modal dialogs for routine read operations; use them for security, destructive confirmation, and credential prompts.
