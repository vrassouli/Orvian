# ADR 0004: Network settings provider safety

## Status

Accepted by explicit product-owner request on 2026-08-02.

## Context

Changing an address, prefix, gateway, or resolver can immediately sever the SSH
connection used to perform and audit the operation. Provider behavior also differs
between NetworkManager and Netplan/systemd-networkd. Treating configuration values
as shell text would violate Remotune's structured-command security boundary.

## Decision

Promote Network Settings into the first-party feature set. The initial plugin:

- reads IPv4, IPv6, default routes, and resolver state through bounded, structured
  `ip` and `resolvectl` requests;
- updates NetworkManager connection profiles through one structured `nmcli`
  invocation per address family;
- validates connection name, CIDR prefix, gateway family, and every DNS address;
- classifies profile mutation as destructive and requires elevation, confirmation,
  mandatory audit-start persistence, and redacted output;
- does not activate a modified NetworkManager profile over the current SSH session;
- supports explicitly confirmed Netplan set/generate/apply sequences, accepting that
  applying a new address can terminate the current SSH connection.

The user explicitly owns updating the saved Remotune host endpoint after an address
change. A disconnect during `netplan apply` is reported honestly; it is not treated
as verified success merely because loss of connectivity was expected.

## Consequences

The public mutation descriptor exposes multiple named fields while preserving the
legacy single-field properties as compatibility defaults. NetworkManager changes
are persistent but require deliberate activation from a console or a separately
designed reconnect-safe workflow. Netplan updates are executed as one audited,
resource-locked operation with individually audited structured commands.
