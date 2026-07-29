# AGENTS.md

## Project mission

Build Orvian as a cross-platform, agentless system administration platform using .NET 10, Avalonia, MVVM, SSH.NET, and a plugin-first architecture.

## Non-negotiable architecture rules

1. Plugins must never receive direct SSH session access.
2. Plugins must never receive passwords or raw secrets.
3. Every remote command must pass through `ICommandExecutor`.
4. Every attempted command must be audited before execution and completed afterwards.
5. Features depend on capabilities, not distribution names.
6. Commands are represented as executable plus structured arguments; avoid interpolated shell strings.
7. Sensitive arguments, input, and output must be redacted or omitted according to policy.
8. Plugins register UI contributions as metadata; they do not manipulate the application shell directly.
9. Core projects must not reference concrete feature plugins.
10. Keep contracts small, testable, and platform-neutral.

## Working practices

- Target .NET 10 and keep nullable reference types enabled.
- Treat compiler warnings as errors.
- Add tests for public behavior and security-sensitive logic.
- Document significant architectural decisions in `docs/adr/`.
- Keep commits focused and use descriptive messages.
- Do not add production features during Sprint 0 unless they validate an architectural boundary.

## Repository layout

- `src/`: core platform projects
- `plugins/`: first-party plugins
- `tests/`: automated tests
- `docs/`: architecture, roadmap, ADRs, and SDK documentation

## Definition of done

A change is complete when it builds in Release mode, tests pass, public contracts are documented, secrets cannot leak through logs, and plugin/core dependency boundaries remain intact.
