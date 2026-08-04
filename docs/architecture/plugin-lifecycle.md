# Plugin Lifecycle

## Trust model

MVP plugins execute in the Remotune process. They are trusted installed code, not securely sandboxed. The permission system controls access to Remotune services and documents intent, but cannot defend against deliberately malicious in-process code using the .NET runtime or operating-system APIs.

Only application-controlled plugin directories are scanned. Installation must be explicit. Strong signing, process isolation, and marketplace trust are future work.

## Package layout

A plugin directory contains:

- `orvian.plugin.json` manifest.
- Entry assembly and declared managed dependencies.
- Optional resource assemblies and assets.
- No executable installer logic.

Plugin data and settings are stored outside the installation directory using a core-provided scoped storage abstraction.

## Manifest requirements

- Stable plugin ID.
- Display name, description, publisher, and semantic version.
- Entry assembly and entry type.
- Compatible Remotune API version range, represented by the legacy `orvianApiVersion` manifest field for compatibility.
- Declared permissions.
- Required and optional capabilities.
- Declared contributions and provider metadata when static discovery is useful.
- Optional dependency list with version ranges.

Unknown required manifest fields, malformed IDs, invalid versions, path traversal, duplicate IDs, or incompatible API ranges cause quarantine.

## Lifecycle states

```text
Discovered
 -> Validated
 -> Compatible
 -> Enabled
 -> Loaded
 -> Configured
 -> Active
 -> Stopping
 -> Unloaded
```

Alternative states: `Disabled`, `Incompatible`, `Quarantined`, `Faulted`, `RestartRequired`.

## Startup sequence

1. Scan controlled directories.
2. Parse manifests without loading assemblies.
3. Validate paths, IDs, versions, permissions, and dependencies.
4. Resolve duplicates deterministically; duplicates are quarantined rather than silently overridden.
5. Check Remotune API compatibility.
6. Read persisted enabled state.
7. Load enabled compatible assemblies through a plugin load context.
8. Instantiate the declared plugin entry point.
9. Supply constrained registration builders.
10. Validate all registrations.
11. Commit contributions atomically to registries.
12. Activate optional background services after the shell and core services are ready.

A plugin whose registration fails contributes nothing. Partially committed contributions are prohibited.

## Runtime services available to plugins

Only explicitly approved abstractions, such as:

- Operation and command request service.
- Host/discovery read models.
- Plugin-scoped settings and data storage.
- Navigation and command contribution builders.
- Dialog/notification abstractions.
- Clock, localization, and logging abstractions.
- Cancellation/lifetime token.

Plugins do not receive root dependency injection, SSH transport, database connection/context, raw secret store, host-key decision service, or mutable shell internals.

## Enable and disable

Disabling a plugin:

- Prevents new operations.
- Cancels plugin background activity.
- Removes contributions after active page/operation handling is resolved.
- Disposes plugin-scoped services.
- Attempts assembly unload when supported.
- Marks restart required when safe unload is impossible.

Active remote mutations are not abruptly abandoned. Core operation ownership continues until completion/cancellation, while plugin result rendering may fall back to generic activity UI.

Enabling repeats compatibility validation and lifecycle initialization.

The MVP persists the desired enabled state in SQLite. Explicit disable is
durable across restarts, while process shutdown only unloads runtime state.
Re-enabling a discovered plugin repeats assembly loading, runtime identity
validation, registration, activation, and durable-state recording. A failure to
record enabled state rolls registration back so an apparently disabled plugin
is not left active.

## Update

MVP supports manual replacement while disabled or application closed. Automatic online update is excluded. Update must preserve plugin-scoped data unless migration explicitly succeeds. Permission additions are surfaced and require explicit approval in future installation UI.

## Dependencies

- Circular plugin dependencies are invalid.
- A plugin cannot depend on a concrete first-party feature merely to reuse internal code; shared public abstractions belong in an SDK package.
- Missing optional dependencies disable only related contributions when designed that way.
- Missing required dependencies make the plugin incompatible.

Startup resolves the complete discovered dependency graph before loading any
assembly. Required plugins load before their dependents regardless of directory
order. Missing, incompatible, quarantined, disabled, or faulted prerequisites
leave the dependent `Incompatible` and unregistered. Every member of a cycle is
also incompatible and contributes nothing.

Runtime enable repeats the active-prerequisite check. An active prerequisite
cannot be disabled until its active dependents are disabled; the management UI
names those dependents rather than silently cascading a durable preference
change.

## Fault containment

Plugin callbacks execute through exception boundaries. A failure is logged with plugin identity and correlation ID. The core may disable a repeatedly failing background contribution. Plugin exceptions must not be shown raw to users and must not prevent audit completion.

## Versioning

Before SDK 1.0, contracts may evolve with coordinated first-party updates. After 1.0:

- Semantic versioning applies to public plugin contracts.
- Additive compatible changes increment minor version.
- Breaking changes increment major version.
- Compatibility range is validated before loading.
- Obsolete APIs have documented migration paths when practical.

## Testing requirements

Cover malformed manifests, traversal attempts, duplicate IDs, incompatible API version, dependency cycles, registration rollback, startup exceptions, disabled state, safe disposal, contribution removal, and fault containment.
