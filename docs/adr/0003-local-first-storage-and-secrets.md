# ADR 0003: Local-First SQLite with Platform Secret Stores

- Status: Accepted
- Date: 2026-07-29

## Context

The initial product must operate without a cloud account while storing host inventory, discovery, settings, plugin state, and audit history. Credentials require stronger protection than ordinary application data.

## Decision

Use SQLite for local relational application data and native platform secret stores for credentials. SQLite records contain only opaque secret references. No plaintext fallback is permitted when a platform secure backend is unavailable; session-only credentials may be offered with a clear warning.

Privilege-escalation passwords are session-only in the MVP.

## Consequences

- The application works offline and has no mandatory server.
- Persistence and secret writes need compensating cleanup because they cannot share one transaction.
- Cross-platform secure-storage adapters are required.
- Audit data is local and not claimed to be tamper-proof against a local administrator.
- Cloud sync and enterprise centralized audit remain future additions.

## Rejected alternatives

- Plaintext or reversible credential storage in SQLite.
- Mandatory cloud vault/account.
- Persisting sudo passwords.
- One JSON file for all application and audit data.