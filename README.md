# Orvian

Orvian is a cross-platform, agentless infrastructure administration desktop
application. It connects to managed hosts over SSH and exposes capability-based,
provider-backed workflows instead of a general-purpose terminal.

> Status: pre-release MVP implementation. The macOS preview is the first
> delivery target; release artifacts are currently unsigned and intended only
> for internal testing.

## Implemented MVP surface

- Host inventory backed by SQLite.
- Password and private-key SSH authentication.
- Mandatory first-use and changed-host-key verification.
- Native credential storage on macOS, Windows, and Linux, with an explicit
  session-only fallback when a secure backend is unavailable.
- Capability discovery and cached host facts.
- Centralized permission, confirmation, privilege, timeout, output-bound,
  redaction, and audit enforcement for every remote command.
- Persistent Activity history, bounded search, paging, retention, and local
  redaction-safe diagnostics.
- Host Overview, Date & Time, Services, Users & Groups, and Package Information
  plugins. The MVP intentionally keeps users/groups and packages read-only.

See [the roadmap](docs/roadmap.md), [MVP scope](docs/product/mvp-scope.md), and
[architecture overview](docs/architecture/overview.md) for authoritative detail.

## Security model

Plugins never receive SSH sessions, credentials, secret-store access, database
contexts, or the root service provider. They contribute immutable metadata and
structured command plans. Core services validate permissions and capabilities,
request confirmations and privilege credentials, enforce host identity and
execution policy, and record audit state before transport execution.

Remote commands use an executable plus structured arguments. Shell syntax is
not the normal execution path. Secrets must not be placed in repository files,
command-line arguments, logs, diagnostics, audit records, or test fixtures.

## Build and test

Install the SDK selected by [`global.json`](global.json), then run:

```bash
dotnet restore Orvian.slnx
dotnet build Orvian.slnx --configuration Release --no-restore
dotnet format Orvian.slnx --verify-no-changes --no-restore
dotnet test Orvian.slnx --configuration Release --no-build
dotnet list Orvian.slnx package --vulnerable --include-transitive --no-restore
```

Default tests are deterministic and do not require network access or live
infrastructure. An opt-in read-only Linux compatibility test verifies SSH host
keys, authentication, discovery, and first-party read providers against a
disposable host. Its environment variables and safe invocation are documented
in [Testing Strategy and Definition of Done](docs/development/testing-and-definition-of-done.md#remote-compatibility-tests).

## Run locally

```bash
dotnet run --project src/Orvian.App/Orvian.App.csproj
```

First-party plugins are built and copied into the application output
automatically. Local application state is stored beneath the operating system's
`LocalApplicationData/Orvian` directory; credentials are stored separately in
the platform's native secure store when available.

## macOS preview packaging

The CI workflow publishes unsigned, self-contained `osx-arm64` and `osx-x64`
preview bundles. Local packaging requirements and verification steps are in
[macOS Distribution](docs/development/macos-distribution.md). Signing,
notarization, stapling, and public distribution remain gated on the explicit
release-readiness milestone.

## Repository layout

```text
src/       Application shell, orchestration, contracts, and infrastructure
plugins/   First-party plugins using public SDK boundaries
tests/     Unit, architecture, integration, UI, and opt-in compatibility tests
docs/      Authoritative product, architecture, security, UX, SDK, and backlog docs
eng/       Packaging and engineering scripts
```

## License

A license has not yet been selected. Until one is added, all rights are
reserved.
