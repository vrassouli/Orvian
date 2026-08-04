# ADR 0005: Core-owned audited SFTP file transfer

## Status

Accepted by explicit product-owner approval on 2026-08-03.

## Context

Orvian needs a WinSCP-like dual-pane file manager. File transfer is a remote
operation but not a shell command. Giving a plugin an SSH.NET or SFTP session would
violate the transport boundary, while representing byte transfer as a shell command
would be inaccurate and could weaken argument and secret guarantees.

## Decision

The SSH infrastructure implements a platform-neutral remote-filesystem contract.
Core application services own path validation, operation policy, audit ordering,
progress, cancellation, overwrite/delete confirmation, bounded concurrency, and
partial-result classification. The first-party plugin contributes capability and
navigation metadata only; it never receives a transport object.

SFTP uses the same authenticated, host-key-verified SSH connection lifecycle.
Every remote filesystem attempt receives an audit start before transport access and
a completion update afterwards. Mutations fail closed when audit start persistence
is unavailable. Audit details retain normalized paths and metadata, never file
contents. Recursive operations do not follow symbolic links.

Opening a remote file downloads it to an Orvian-owned temporary directory and asks
the local operating system to open it with the registered application. Temporary
files are removed on shutdown when possible.

## Consequences

- SSH.NET types remain isolated in `Orvian.Ssh`.
- Transfers have item and aggregate byte progress and are cancellation-aware.
- A disconnected or replaced connection invalidates file operations.
- SFTP subsystem absence is an explicit unsupported state and does not weaken SSH
  host identity checks.
- SCP, remote-to-remote transfer, resumable transfer, synchronization, and conflict
  merging remain future work.
