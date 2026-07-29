# ADR 0002: Centralized Command, Security, and Audit Pipeline

- Status: Accepted
- Date: 2026-07-29

## Context

Remote administration is security-sensitive. Allowing each plugin to manage SSH, sudo, quoting, credentials, logging, and error handling would create inconsistent behavior and secret leakage risk.

## Decision

Every remote command, including discovery and background work, passes through a core-owned operation/command pipeline. Plugins submit structured executable/argument requests and declared privilege/permission metadata. The core owns validation, confirmation, privilege escalation, credentials, transport, output limits, redaction, cancellation, and audit.

Mutation commands fail closed when mandatory audit-start persistence is unavailable.

## Consequences

- Plugins cannot access SSH sessions or raw secrets.
- All execution receives consistent security and audit behavior.
- The pipeline is a high-value, heavily tested subsystem.
- Exceptional shell syntax requires an explicit restricted abstraction.
- AI and future automation sources cannot bypass user/policy controls.

## Rejected alternatives

- Exposing SSH.NET sessions through plugin context.
- Letting plugins prepend `sudo` or prompt for passwords.
- Logging only after successful command execution.
- Treating raw command strings as the primary API.