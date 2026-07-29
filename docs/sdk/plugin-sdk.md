# Plugin SDK

## Goal

The SDK lets a plugin add infrastructure features without receiving SSH sessions, credentials, persistence implementations, or shell internals. First-party plugins must use the same contracts.

## Plugin entry point

A plugin exposes one entry type implementing `IOrvianPlugin` with:

- Immutable manifest identity.
- Synchronous registration of static contributions and service descriptors.
- Optional asynchronous activation through a lifecycle interface.
- No remote calls during registration.

Registration must be deterministic and side-effect free. It may validate local metadata but must not access hosts, secrets, network, or database implementations.

## Manifest

Recommended ID format: reverse-domain or project-owned dotted ID, for example `io.orvian.services` or `orvian.services` for official plugins.

Manifest fields:

- `id`
- `name`
- `description`
- `publisher`
- `version`
- `orvianApiVersion`
- `entryAssembly`
- `entryType`
- `permissions`
- `dependencies`
- optional homepage/license metadata

Manifest identity must match runtime plugin identity. Plugin ID cannot be changed without being treated as a new plugin.

## Contributions

### Navigation page

A page contribution declares:

- Stable contribution ID and route.
- Title and localization key.
- Icon descriptor.
- View/view-model factory or approved type descriptor.
- Required capabilities.
- Optional feature/provider association.
- Sort/group metadata.

Routes are plugin-scoped and cannot shadow core routes.

### Commands

UI/command-palette contribution declares:

- Stable command ID.
- Label/description/icon.
- Availability predicate over safe read models.
- Operation factory.
- Required capabilities and permissions.
- Risk/confirmation metadata supplied as input to core policy, never used to weaken policy.

### Settings

Plugin settings are scoped by plugin ID and versioned. Plugins receive typed scoped storage. They do not access global settings or SQLite directly.

### Discovery probes

A probe declares:

- Stable probe ID.
- Read-only command factory.
- Preconditions and timeout.
- Output logging sensitivity.
- Fact parser and produced fact/capability descriptors.

Probes must be safe, noninteractive, idempotent, and independently failure-tolerant.

### Feature providers

A provider declares:

- Feature ID.
- Provider ID and priority.
- Required facts/capabilities.
- Compatibility predicate.
- Operations/read models it implements.
- Optional explanation metadata.

Providers produce structured command requests through core services. They do not execute transport calls.

## Runtime context

A plugin runtime context may expose:

- Plugin identity/version.
- Lifetime cancellation token.
- Operation service.
- Host and discovery read-only services.
- Scoped settings/data service.
- Approved logging abstraction with automatic plugin correlation.
- Localization and clock abstractions.
- Core-owned dialog/notification service where appropriate.

The context must not expose root service provider, secret retrieval, transport, SQLite, or internal shell registries.

## Commands from plugins

A plugin should:

1. Validate feature input semantically.
2. Resolve the active provider from the capability/provider service.
3. Create an operation intent with purpose, risk, permission, and resource lock.
4. Create structured command requests using separate arguments.
5. Mark sensitive arguments/stdin.
6. Parse structured/bounded output defensively.
7. Return a feature result or classified parse failure.

A plugin must not:

- Concatenate user input into shell text.
- Prepend `sudo` itself.
- Read password/passphrase values.
- Claim success solely from no exception.
- Retry mutations independently of core policy.
- Write audit entries directly to simulate execution.

## Capability naming

Capabilities are lowercase dotted identifiers. General semantic capability precedes implementation detail:

- `service.read`
- `service.manage`
- `init.systemd`
- `time.read`
- `time.timezone.write`
- `privilege.sudo`

Do not create distribution capabilities such as `ubuntu.service.manage` unless a truly distribution-specific semantic behavior cannot be represented through facts/provider matching.

## Error behavior

Plugin public callbacks return structured result types or throw only for programmer/unexpected failures. Expected remote failures remain command/operation results. Parser errors include safe messages and diagnostic context without secrets.

## Threading

- Registration is synchronous and must be fast.
- Remote/application operations are asynchronous.
- Plugins do not assume callbacks occur on the UI thread.
- UI state changes use approved dispatching abstractions.
- Background services observe plugin lifetime cancellation.

## Compatibility rules

- Do not reference internal Orvian assemblies.
- Use only documented public SDK packages.
- Do not depend on concrete first-party plugins.
- Avoid reflection into core internals.
- Public serialized plugin data has an explicit schema version and migration path.

## Minimum plugin tests

- Manifest validity.
- Registration and contribution uniqueness.
- Provider selection fixtures.
- Command construction with hostile input.
- Permission/capability availability.
- Output parser: normal, malformed, localized, empty, truncated, permission denied.
- Cancellation and plugin disposal.
- No forbidden package references through architecture tests.

## Sample plugin expectation

The repository sample plugin must demonstrate:

- Valid manifest and registration.
- One navigation contribution.
- One read-only capability/provider.
- One command executed through the operation service.
- Defensive parser.
- Tests using fake command execution.

It must not remain a page that only displays static text once the executable pipeline is available.