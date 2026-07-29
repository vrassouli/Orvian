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

Validation is inline and does not erase entered data. `Test connection` uses the same verification/authentication services but does not silently persist trust or secrets without explicit choices.

## Connection flow

Connection progress uses meaningful stages: resolving, connecting, verifying identity, authenticating, and discovering. The user can cancel.

Unknown host key dialog displays:

- Requested host and resolved address when available.
- Key algorithm.
- SHA-256 fingerprint.
- Explanation that first trust should be verified through an independent channel for sensitive systems.
- `Trust and continue` and `Cancel` actions.

Changed host key dialog is visually severe, defaults to cancel, shows previous and new fingerprints, and requires an explicit replace-trust action. It must not be a routine one-click warning.

Authentication errors preserve the profile and offer retry/edit options. Network and identity errors are distinct.

## Host overview

Show:

- Display name, address, verified fingerprint summary.
- Connected/disconnected/interrupted state.
- OS family/distribution/version, architecture, kernel, hostname, uptime.
- Discovery freshness and partial-failure indicator.
- Available privilege provider.
- Capability summary and active provider explanations.
- Actions: connect/disconnect, refresh discovery, edit host, view activity.

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

Command details include executable, redacted arguments, exit code, timing, truncation state, output according to policy, and safe failure classification. Diagnostic discovery commands are hidden by default from the user-action view but available through a diagnostic filter.

## Plugin management

Show installed plugin name, publisher, version, state, compatibility, permissions, and errors. Enable/disable actions explain restart requirements. Do not imply third-party plugins are sandboxed.

## Error presentation

- User message: concise, specific, and actionable.
- Technical details: expandable, redacted, includes correlation ID.
- Raw stack traces are not shown in normal UI.
- Copy details uses the same redacted representation.
- Persistent failures should not produce repeated notification spam.

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