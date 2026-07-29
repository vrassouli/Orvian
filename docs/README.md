# Orvian Documentation Map

This directory is the authoritative product and engineering specification for Orvian. Agents and contributors must read the documents required by their task before changing code.

## Reading order for every agent

1. `/AGENTS.md`
2. `product/vision.md`
3. `product/product-requirements.md`
4. `product/mvp-scope.md`
5. `architecture/overview.md`
6. `architecture/project-boundaries.md`
7. `ux/ui-ux-architecture.md` for every shell, navigation, page, plugin UI, responsive, or presentation task
8. The task-specific architecture, security, UX, and SDK documents
9. `development/agent-workflow.md`
10. `development/definition-of-done.md`
11. The relevant sprint backlog and ADRs

## Authority and conflict resolution

When documents conflict, use this precedence:

1. Accepted ADRs
2. Security requirements
3. Product requirements and MVP scope
4. Architecture specifications
5. SDK contracts
6. UX specifications
7. Sprint backlog
8. Examples and tutorials

Do not silently resolve a conflict. Record it in the PR and propose an ADR or documentation correction.

## Directory guide

- `product/`: mission, users, requirements, scope, and user journeys.
- `architecture/`: system structure and runtime behavior.
- `security/`: threat model and mandatory security policies.
- `ux/`: application behavior visible to users, including the authoritative cross-platform Settings-style UI architecture.
- `sdk/`: plugin authoring contracts and conventions.
- `development/`: contributor and agent execution rules.
- `adr/`: accepted architectural decisions.
- `backlog/`: executable sprint plans and task templates.

## Definition of agent-ready

A task is ready for autonomous implementation only when it provides:

- A concrete outcome and rationale.
- In-scope and out-of-scope statements.
- References to governing documents.
- Acceptance criteria that can be verified.
- Required tests.
- Security and compatibility considerations.
- Expected files or project boundaries, when known.

An agent must not invent missing product behavior that materially affects security, compatibility, data persistence, public plugin contracts, or the cross-platform information architecture.
