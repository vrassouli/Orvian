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
- `file.read`
- `file.write`

A plugin declares permissions in its manifest. Registration and runtime execution both verify them. Permission declaration does not authorize an operation by itself; host state, capability, risk, user confirmation, and policy are re-evaluated for every execution.

New broad permissions require review. Prefer narrow semantic permissions over an unrestricted `admin` permission.

## Host identity trust

Host-key trust is core-owned and unavailable to plugins. A trusted association
binds the host profile ID, hostname, port, resolved IP address, key algorithm,
and SHA-256 fingerprint. A change to any association field blocks automatic
continuation and requires an explicit replace-trust decision. Records created
before resolved-address persistence intentionally require one revalidation
instead of silently accepting an unknown prior address.

## Secret store

`ISecretStore` exposes scoped operations using opaque identifiers. It does not expose enumeration of all secrets to plugins.

Secret kinds include:

- SSH password.
- Private-key material or secure reference to key file, depending on platform policy.
- Private-key passphrase.
- Future protocol credentials.

Privilege passwords are session-only and not persisted in MVP.

Secret metadata may contain non-sensitive labels, creation/update time, kind, and owning host profile. Metadata must not reveal the secret value.

Host editing exposes only whether an opaque remembered-credential reference
exists; it never retrieves or displays the credential. Keeping, replacing, and
removing that reference are distinct explicit choices. Replacing it with a
session-only credential removes the old secure-store entry after the profile
update, and a failed cleanup is surfaced as a safe warning rather than being
reported as complete success.

## Platform backends

- Windows Credential Manager generic credentials, persisted for the current
  Windows user through `CredWriteW`/`CredReadW`/`CredDeleteW`.
- macOS Keychain generic passwords.
- Linux Secret Service-compatible keyring through the trusted system
  `secret-tool` executable.

When no secure backend is available, Remotune must not silently fall back to plaintext storage. It may offer session-only use with a clear warning.

The composition root selects only the native backend for the current operating
system. A missing Linux `secret-tool` produces the explicit unavailable store;
Remotune never searches an untrusted `PATH` entry or creates a file fallback.
Linux secrets are sent through redirected stdin until EOF and retrieved from
bounded redirected stdout using structured process arguments and no shell.
Windows credential blobs are copied through bounded unmanaged memory. Owned
managed and unmanaged buffers are cleared after use, including native
Credential Manager retrieval buffers before `CredFree`.

The host editor receives only the backend-availability flag. When persistent
storage is unavailable, the remember control is disabled, the session-only
lifetime is stated explicitly, and a programmatic remember request fails before
profile persistence. A credential entered without remembering is held only in
the disposable session cache; the host profile contains no secret reference.

Current backend limits are 2,560 UTF-8 bytes for Windows Credential Manager and
8,191 UTF-8 bytes for the `secret-tool` path. Oversized values fail before
native access. Backend errors expose only the operation and numeric status, not
credential values or backend diagnostic text.

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

The current core providers use:

- direct structured execution when discovery confirms the remote account is
  already root;
- `sudo -n --` when no session credential is present;
- `sudo -S -p "" --` with a disposable stdin buffer when the user supplied a
  connection-scoped credential;
- non-interactive `doas -n` execution. Password-prompting doas variants remain
  unsupported because portable doas does not provide a consistent safe stdin
  password contract.

Privilege credentials use a cache separate from SSH authentication credentials
and are cleared on disconnect, host deletion/edit disconnect, or application
shutdown.

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

The MVP local diagnostic implementation accepts only mapped
`SafeDiagnosticEvent` values. It validates event/category/property bounds,
removes line breaks, redacts sensitive property names, credential-like
assignments, URI user information, and PEM-shaped material as defense in depth,
and never receives raw exception objects. Diagnostics use bounded rolling JSON
lines files with owner-only Unix permissions where supported. An I/O failure in
diagnostic logging does not crash or change the result of the operation being
reported.

## Output sensitivity

Providers classify expected output sensitivity. Examples:

- Service status: usually full after control-sequence sanitation.
- `/etc/shadow`, key material, token files: prohibited operations or metadata-only.
- Configuration files that may contain secrets: redacted or disabled output logging.

A plugin cannot request `Full` logging when core policy requires a stricter mode. Core may always downgrade retention.

The command pipeline persists only its effective core-selected mode. `Full`
retains bounded output after transport sanitation. `Redacted` replaces each
non-empty output stream with the fixed `[REDACTED]` marker rather than applying
best-effort pattern substitution. `MetadataOnly` and `Disabled` persist no
stream content. Output byte counts and truncation state remain metadata and may
be retained under a longer policy. Current plugin discovery, read, and mutation
adapters select `Redacted`; plugins do not control this persistence decision.

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
