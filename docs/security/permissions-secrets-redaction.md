# Permissions, Secrets, and Redaction

## Plugin permissions

Permissions are stable lowercase identifiers. Initial categories include:

- `host.read`
- `discovery.contribute`
- `command.read.execute`
- `command.mutate.execute`
- `command.shell.execute`
- `privilege.elevated.request`
- `ui.navigation.contribute`
- `ui.command.contribute`
- `settings.plugin.readwrite`
- `background.read`

A plugin declares permissions in its manifest. Registration and runtime execution both verify them. Permission declaration does not authorize an operation by itself; host state, capability, risk, user confirmation, and policy are re-evaluated for every execution.

New broad permissions require review. Prefer narrow semantic permissions over an unrestricted `admin` permission.

## Secret store

`ISecretStore` exposes scoped operations using opaque identifiers. It does not expose enumeration of all secrets to plugins.

Secret kinds include:

- SSH password.
- Private-key material or secure reference to key file, depending on platform policy.
- Private-key passphrase.
- Future protocol credentials.

Privilege passwords are session-only and not persisted in MVP.

Secret metadata may contain non-sensitive labels, creation/update time, kind, and owning host profile. Metadata must not reveal the secret value.

## Platform backends

- Windows Credential Manager or suitable Windows secure credential API.
- macOS Keychain.
- Linux Secret Service-compatible keyring.

When no secure backend is available, Orvian must not silently fall back to plaintext storage. It may offer session-only use with a clear warning.

## Secret lifetime

- Retrieve only when required.
- Keep in the narrowest scope possible.
- Do not convert to immutable strings unnecessarily.
- Dispose/clear buffers where APIs permit, while acknowledging .NET cannot guarantee all memory erasure.
- Never include a secret in command-line arguments when stdin or protocol authentication is available.

## Sudo/doas handling

- Core owns the prompt and credential flow.
- Password is supplied through a non-echoing stdin path or protocol appropriate to the provider.
- Plugin receives only success/failure.
- Cache is limited to the current application/connection session and bounded by provider behavior and local policy.
- `sudo -S` output/prompt text must be handled without storing the password.
- Never log stdin for privilege commands.

## Redaction model

Redaction happens at multiple layers:

1. Request metadata marks sensitive arguments/stdin.
2. Core structural redaction replaces marked values before audit creation.
3. Pattern-based defensive redaction catches common credential formats in logs/output.
4. Feature-specific redactors may remove domain secrets from returned output.
5. UI uses already-redacted audit read models.

Structural redaction is authoritative; regex/pattern redaction is defense in depth and cannot be the only protection.

Use a consistent replacement such as `[REDACTED]`. Do not preserve secret length unless explicitly safe.

## Logging rules

Never log:

- Passwords, passphrases, private keys, tokens, or privilege stdin.
- Entire authentication objects.
- Raw exception objects from libraries when they may embed credentials; map and sanitize first.
- Unredacted command requests.
- Secret-store values or backend payloads.

Safe logging may include secret reference ID only when it is not itself sensitive, secret kind, backend status, and correlation ID.

## Output sensitivity

Providers classify expected output sensitivity. Examples:

- Service status: usually full after control-sequence sanitation.
- `/etc/shadow`, key material, token files: prohibited operations or metadata-only.
- Configuration files that may contain secrets: redacted or disabled output logging.

A plugin cannot request `Full` logging when core policy requires a stricter mode. Core may always downgrade retention.

## Confirmation and permission UX

Confirmation shows:

- Plugin/feature name.
- Target host.
- Operation purpose.
- Risk classification.
- Requested privilege.
- High-level effect.

Do not display secrets or overwhelming raw command text by default. An advanced details section may show redacted executable/arguments.

## Tests

- Every secret kind has storage/retrieval/delete tests against abstractions and platform integration where available.
- Redaction tests include substrings, duplicate values, unicode, empty secrets, output boundaries, truncated output, exception mapping, and serialization.
- Permission tests ensure UI contribution, registration, and execution checks cannot be bypassed.
- No snapshot test fixture may contain real credentials.