# Plugin SDK

## Goal

The SDK lets a plugin add infrastructure features without receiving SSH sessions, credentials, persistence implementations, or shell internals. First-party plugins must use the same contracts.

## Plugin entry point

A plugin exposes one entry type implementing the legacy-compatible `IOrvianPlugin` contract with:

- Immutable manifest identity.
- Synchronous registration of static contributions and service descriptors.
- Optional asynchronous activation through a lifecycle interface.
- No remote calls during registration.

Registration must be deterministic and side-effect free. It may validate local metadata but must not access hosts, secrets, network, or database implementations.

## Manifest

Recommended ID format: reverse-domain or project-owned dotted ID. Existing official
plugins retain their `orvian.*` IDs because plugin identity is persistent and cannot
be renamed safely in place. New third-party plugins should use a publisher-owned ID.

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
Read-only feature pages use
`INavigationRegistry.AddFeaturePage(..., featureId)`. The shell renders these
entries from the contribution catalog and invokes the matching
`IReadOnlyFeatureProvider`; it does not contain first-party plugin IDs or
routes. `AddPage` remains available for pages that will supply their own
approved view-model integration.

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

### File transfer contribution

The official File Transfer plugin is a declarative navigation contribution with
`file.read` and `file.write` permissions. These permissions do not expose a file
transport API to plugin code. The shell recognizes the stable `file-transfer`
feature contract and invokes the core-owned transfer application service, which
applies host connection, confirmation, audit, path, progress, cancellation, and
partial-result policy. Third-party SFTP clients and direct session access are not
part of the public SDK.

The current read-provider contract is `IReadOnlyFeatureProvider`. Registration
uses `IPluginBuilder.Features.Add`. A provider supplies:

- stable feature/provider IDs and deterministic priority;
- required semantic capabilities;
- a `PluginReadRequest` containing one executable, structured arguments, and a
  bounded timeout;
- a defensive parser from bounded `PluginCommandOutput` to an immutable
  `PluginFeatureReadModel`.

The application selects the highest-priority compatible provider (provider ID
is the stable tie-breaker), supplies plugin identity and declared permission to
the core command pipeline, refuses truncated output before parsing, and converts
provider exceptions to safe failures. Providers never receive an executor,
connection, SSH session, audit sink, secret store, database, or service
provider.

Features built entirely from already-authorized local host metadata implement
`ILocalHostFeatureProvider`. They require `host.read`, not command-execution
permission. Core supplies an immutable `LocalHostFeatureContext` containing
only display name, endpoint, connection state, trusted public host-key
fingerprint, normalized discovery facts/capabilities, discovery freshness, and
the core clock value used to render the model. The context deliberately omits
username, authentication method, credential references, private-key paths,
tags, notes, repositories, database access, sessions, transports, and secret
services. Local providers return the same immutable read model and are selected
by capability, priority, and provider ID without creating a synthetic remote
command or audit entry.

Features that require more than one independent read implement
`IMultiCommandReadFeatureProvider`. They return between one and eight
structured requests. Core assigns one operation identity, persists the
operation aggregate, executes and audits each request in order, stops on the
first failure, rejects any truncated child output, and only then supplies the
complete bounded output sequence to the plugin parser.

Parameterized user-initiated reads implement `IParameterizedReadFeatureProvider`.
They declare stable action metadata and parameters, validate every supplied value,
and return one bounded structured read request. Core selects them by capability,
executes each refresh as an informational audited operation, and never routes them
through mutation permission or confirmation policy. This supports bounded polling
experiences such as container logs without introducing unbounded remote processes.

Mutation providers implement `IMutationFeatureProvider` and register through
the same feature registry. They declare stable feature/mutation/provider IDs,
semantic capability requirements, parameter metadata, risk, and whether
elevation is required. `CreateRequest` receives a keyed parameter dictionary
and must validate every value before returning an executable plus structured
arguments. Core rechecks capability compatibility, manifest permissions, risk
consistency, connection identity, confirmation, privilege, audit availability,
timeout, and output policy at execution time.

The shell presents every compatible mutation descriptor returned for a feature;
first-party feature routes and action IDs are not hardcoded into the shell.
Mutation resource locking is conservatively scoped to the host and feature so
conflicting actions cannot run concurrently even when they use different
provider action IDs.

Mutation providers may opt into bounded result display with `DisplaysOutput`.
The shell shows at most 64 KiB of the already sanitized command standard output
and does not perform the normal post-mutation discovery refresh for that action.
This is intended for conservative compatibility shims such as a bounded Docker
logs action until parameterized read actions are available; it does not weaken
mutation permission, confirmation, audit, timeout, or output-limit enforcement.

## Runtime context

A future richer plugin runtime context may expose:

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
- `time.datetime.write`
- `privilege.sudo`
- `container.docker`
- `docker.read`
- `docker.manage`

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

- Do not reference internal Remotune assemblies (currently named `Orvian.*` for compatibility).
- Use only documented public SDK packages.
- Do not depend on concrete first-party plugins.
- Avoid reflection into core internals.
- Public serialized plugin data has an explicit schema version and migration path.

## Rebrand compatibility

Remotune is the product name. The `Orvian.*` assembly and namespace names,
`IOrvianPlugin`, `orvian.plugin.json`, `orvianApiVersion`, and existing `orvian.*`
plugin IDs are legacy public compatibility identifiers. They remain unchanged so
existing plugins and persisted plugin state continue to work. Changing any of these
requires a separately versioned SDK migration and compatibility plan.

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
