# Persistence

## Technology and ownership

SQLite is the initial local relational store. `Orvian.Persistence` owns database access, schema migrations, transactions, and repository implementations. Domain and application projects define repository contracts and persistence-agnostic models.

Remotune continues to use the legacy `Orvian` local-data directory and database
filename so an application update does not strand existing profiles, audit history,
settings, trusted host keys, or plugin state.

The database never stores plaintext passwords, private-key contents, key passphrases, or privilege passwords. It stores opaque references to `ISecretStore` entries.

## Data groups

### Host profiles

Persist:

- Host profile ID.
- Display name, hostname/address, port, username.
- Authentication method and secret reference IDs.
- Private-key file path when key authentication is selected; never key contents.
- Tags, notes, enabled state, connection preferences.
- Trusted host-key records and verification timestamps.
- Created and updated timestamps.

Use normalized child tables or robust serialization for tags/settings; schema changes require migrations.

Host duplication creates a new profile row and copies only ordinary profile
fields. Secret references, trusted host keys, discovery snapshots, and
connection state remain associated with the original stable host ID and are not
copied.

### Discovery

Persist immutable discovery snapshots and a pointer to the latest snapshot per host. Store normalized facts, capabilities, probe outcomes, provenance, freshness, and partial/completed status. Old snapshots are subject to retention policy.

### Plugins

Persist plugin ID, observed version, enabled state, compatibility/quarantine
status, safe diagnostics, permission approval state when introduced, and
plugin-scoped settings metadata. Plugin installation files are not database
blobs.

The MVP `plugin_states` table stores the durable desired enabled state and the
latest observed lifecycle result. A first discovery defaults to enabled. Normal
application shutdown unloads plugins without changing that preference; an
explicit user disable is persisted before contributions are removed. If an
enable-state write fails, the host fails closed and rolls back newly registered
contributions.

### Audit

Persist operation records and command-attempt records. Command output may be stored in bounded text/blob columns or a separate output table so metadata queries do not load large content. Record truncation, redaction, encoding, sizes, and hashes only when policy permits.

The current command-attempt schema is introduced by migration 4. It stores one
row per command ID: the mandatory start insert is committed before transport
execution, and completion is an identity-matched update of that pending row.
Arguments are structurally redacted before serialization. Host deletion does
not cascade audit history. Discovery attempts use the same table and are hidden
from the default activity query unless diagnostic activity is requested.

Activity reads are bounded and paged (default 100, maximum 500) and use indexed
host, plugin, source, status, privilege, risk, and half-open start-time filters
without loading command output. Command risk filtering joins only by operation
identity. Invalid or reversed time ranges fail before executing a query.
Timestamp ordering has a stable identifier tie-breaker so offset pages remain
deterministic.

Migration 6 adds durable operation aggregates with identity-matched start and
completion writes, indexed bounded queries, risk/privilege metadata, command
counts, and terminal integrity status. The application writes the operation
start before confirmation/execution and never reports an operation as fully
successful when completion persistence fails. Activity presents operation
intentions by default and exposes child command attempts as diagnostics.

Migration 7 adds the effective output-logging mode and nullable bounded retained
standard-output and standard-error fields to command attempts. `Full` retains
sanitized command output, `Redacted` retains only the fixed `[REDACTED]` marker
for a non-empty stream, and `MetadataOnly` and `Disabled` retain no stream
content. Byte counts and truncation metadata remain available independently.
Command detail reads load retained content only by command ID; activity-list
queries do not load it. The Activity surface renders operation intentions by
default, can include diagnostic command attempts, and exposes command timing,
exit code, redacted executable/arguments, byte/truncation metadata, failure
classification, effective output policy, and policy-retained streams.

The unified Activity read model merges independently paged operation,
standalone-command, and host-trust streams. Its cursor carries one offset per
stream and the UI freezes an upper timestamp boundary when filters are applied,
so newly started activity does not shift an in-progress page sequence. Command
queries can exclude every command backed by an operation aggregate using a
parameterized `NOT EXISTS`; this keeps child attempts hidden consistently
across pages until the diagnostic option is enabled. Each source fetches only
one bounded look-ahead row beyond the requested page.

Audit retention cleanup is an explicit bounded transactional service. It
deletes only completed records older than the supplied cutoff, never deletes an
operation with a pending command attempt, and writes a durable local cleanup
operation in the same transaction. A separate output cutoff can purge retained
stream content before command and operation metadata expires.

### Application settings

Persist typed settings with schema/version metadata. Security-sensitive settings must have validated defaults. Unknown settings survive downgrade/upgrade only when safe.

File Transfer stores its last valid local directory as a global noncritical
preference and its last valid remote directory per host profile. Paths are bounded,
reject control characters, contain no file content or credentials, and are ignored
when corrupt or written by a newer schema version. A missing local directory or an
unavailable remote directory falls back safely without blocking application startup.

## Transaction boundaries

Use a transaction for:

- Creating/updating a host profile plus related non-secret references.
- Trusting or replacing a host key plus local security event.
- Committing a discovery snapshot and updating the latest pointer.
- Committing plugin registration state changes.
- Creating an operation and its initial state.
- Completing command audit metadata and operation aggregate state when logically coupled.

Secret-store writes and SQLite writes cannot share a distributed transaction. Use compensating behavior:

1. Create/update secret.
2. Persist reference.
3. On persistence failure, attempt to remove newly orphaned secret.
4. On delete, first remove profile reference transactionally, then remove unreferenced secret; retain diagnostic evidence if cleanup fails.

## Migrations

- Migrations are ordered, idempotent at the runner level, and applied before repositories are used.
- Database backup is created before destructive or complex migrations when practical.
- A failed migration prevents normal startup into mutable mode and offers diagnostics/recovery; it must not silently recreate and lose data.
- Migration code is tested from supported previous schema versions.

Migration 10 creates `host_trust_audit_events`, indexed by occurrence time and
host. `SqliteTrustedHostKeyRepository.StoreAsync` requires the matching event
and commits both records atomically. The event describes the explicit
first-trust or replacement decision; it does not claim that subsequent SSH
authentication or discovery succeeded.

Migration 11 creates the key-based `application_settings` table. Each known
setting carries a schema version and invariant JSON value. Saving validates the
complete typed model and upserts all known values in one transaction while
leaving unknown keys untouched. Invalid or newer known records activate
conservative defaults with a user-visible diagnostic and remain preserved until
the user explicitly saves compatible settings.

Migration 12 adds the authenticated remote username to operation and command
audit rows. Legacy records use the explicit `[unknown]` marker; new execution
resolves the value from the core-owned host profile and fails closed if it is
missing or invalid. Start persistence validates the value, and completion
updates identity-match it so a terminal write cannot change the audited user.

Migration 13 adds FTS5 indexes for bounded Activity search over safe metadata:
authenticated username, plugin, operation title/purpose, command
executable/redacted arguments, and host-trust host/address/algorithm/fingerprint
fields. Search text is limited to 200 characters and converted to a
parameterized conjunction of literal terms; callers cannot inject FTS
operators. Retained command output, safe failure detail, secrets, and
unredacted arguments are not indexed. Insert and delete triggers keep indexes
aligned with audit retention, including databases upgraded from earlier
schemas.

- Plugin-scoped persistent schemas use core-approved versioned storage, not arbitrary direct SQLite access.

## Integrity and recovery

- Enable foreign keys.
- Use WAL mode when platform behavior and tests support it.
- Configure busy timeout and bounded retry for lock contention.
- Use UTC `DateTimeOffset`/integer representation consistently.
- Stable identifiers are generated by application code.
- Corrupt noncritical settings may reset with user-visible diagnostics; corrupt host/audit records are not silently discarded.
- Provide a future-safe export/backup boundary even though full backup UI is not MVP.

## Retention

Defaults are policy values, not hard-coded into feature plugins:

- Audit metadata retained until user-configured cleanup; initial default may be 90 days.
- Captured output may have a shorter default retention than metadata.
- Discovery snapshots retain latest plus a bounded history.
- Diagnostic application logs use rolling files independent of SQLite audit retention.

Cleanup is explicit, transactional in batches, cancellation-aware, and itself logged locally.

## Query behavior

Audit and host lists use paging and indexed filters. Never load all command
output to render a list. Host persistence filters text, tags, enabled state,
latest discovered operating-system family, and latest discovered capabilities
with parameterized `EXISTS` queries. Live connection state is not persisted and
is filtered against the application connection snapshot after a bounded host
query. Index likely filters: host ID, timestamps, operation status, plugin ID,
invocation source, and command operation ID.

## Testing requirements

- Clean database creation.
- Migration from every supported schema baseline.
- Transaction rollback.
- Foreign-key integrity.
- Concurrent read/write behavior.
- Secret-reference compensation.
- Retention cleanup.
- Audit paging without output eager loading.
- Recovery behavior for failed migration and malformed settings.
