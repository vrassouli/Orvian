# ADR 0001: Plugin-First, Capability-Based Architecture

- Status: Accepted
- Date: 2026-07-29

## Context

Orvian must support heterogeneous operating systems and appliances without scattering distribution checks throughout the product or coupling the core to every feature.

## Decision

Features are delivered as plugins and depend on semantic capabilities. Target-specific providers implement a feature for discovered facts/capabilities. Core services compose plugins and enforce lifecycle, security, execution, audit, and UI contribution policy.

Feature code must not branch directly on distribution names except inside discovery/provider compatibility logic where those facts are legitimate provider evidence.

## Consequences

- New platforms can add providers without changing feature navigation or core execution.
- Capability identifiers and provider selection become public architectural contracts.
- Discovery quality is critical.
- Duplicate/ambiguous providers require deterministic handling.
- First-party plugins must obey the same contracts as third-party plugins.

## Rejected alternatives

- Distribution-specific feature implementations wired directly into the shell.
- One monolithic remote-management service containing all commands.
- Plugins with direct SSH access.