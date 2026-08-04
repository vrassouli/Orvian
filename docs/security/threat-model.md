# Threat Model

## Assets

- Remote host credentials and private keys.
- Privilege-escalation credentials.
- Trusted host identities.
- Remote command intent and arguments.
- Command output that may contain sensitive infrastructure data.
- Host inventory and topology.
- Plugin installation and enabled state.
- Audit integrity and local application data.

## Trust boundaries

1. User and Remotune UI.
2. Remotune core and installed plugin code.
3. Core and operating-system secret store.
4. Core and local SQLite/filesystem.
5. Core SSH transport and remote host/network.
6. Plugin package files and plugin host.

MVP plugins run in-process and are trusted code after explicit installation. Plugin permissions are not a security sandbox. This limitation must be communicated and must not be misrepresented in UI or documentation.

## Threat actors

- Network attacker intercepting or redirecting SSH traffic.
- Compromised or impersonated remote host.
- Malicious input returned by a remote host.
- Buggy or malicious installed plugin.
- Local unprivileged process or user attempting to read stored data.
- User mistake during a privileged or destructive operation.
- Accidental secret leakage through logging, exceptions, output, or UI.

## Principal threats and mitigations

### Man-in-the-middle and host impersonation

Mitigations:

- Mandatory host-key verification.
- Strong warning and default block on changed keys.
- SHA-256 fingerprint display.
- Core-only trust decisions and auditable replacements.
- No global ignore-host-key option.

### Credential disclosure

Mitigations:

- Platform secret stores.
- Opaque secret references in persistence.
- Core-owned prompts.
- No plugin access to raw secrets.
- No privilege-password persistence in MVP.
- Redaction in logs, audit, exceptions, and UI.
- Minimum-lifetime handling and disposal where possible.

### Command injection

Mitigations:

- Executable plus structured argument model.
- Centralized POSIX escaping.
- No plugin pre-quoting or string interpolation of untrusted values.
- Explicit, restricted shell-command exception path.
- Validation of executable, working directory, environment, and control characters.
- Adversarial quoting tests.

### Unauthorized privileged execution

Mitigations:

- Manifest permission declaration.
- Core policy enforcement at execution time.
- Core-owned confirmation based on risk and privilege.
- Privilege provider controlled by core.
- Audit before execution.
- No trust in UI visibility as authorization.

### Secret leakage through command output

Mitigations:

- Output logging modes.
- Bounded retention.
- Feature/provider redaction descriptors plus core redaction.
- Metadata-only/disabled logging for sensitive operations.
- Never echo passwords to command line or retained stdin.
- Sanitized user-facing errors.

### Malicious remote output

Mitigations:

- Treat output as untrusted data.
- Bound size and processing time.
- Strip dangerous control sequences for presentation.
- No HTML/script interpretation.
- Defensive parsers with malformed and truncated input handling.
- Do not use output to construct subsequent commands without validation.

### Malicious plugin

MVP limitation: in-process plugins can use general .NET and OS APIs and therefore cannot be strongly contained.

Mitigations within scope:

- Explicit installation in controlled directories.
- Manifest validation and compatibility checks.
- No direct core secrets/SSH/database APIs.
- Permissions and audit for Remotune-mediated operations.
- Clear publisher/version visibility.
- Quarantine malformed or conflicting plugins.

Future mitigation: package signing, reputation, process isolation, restricted IPC, and sandboxing.

### Local database/file disclosure

Mitigations:

- Never store credentials in SQLite.
- Rely on OS account filesystem protections.
- Avoid sensitive output storage by default where unnecessary.
- Configurable retention and cleanup.
- Future database encryption may be evaluated through ADR; do not claim encryption at rest unless implemented.

### Audit tampering or gaps

MVP local administrators can modify local files; tamper-proof audit is not claimed.

Mitigations:

- Audit start before execution.
- Fail closed for mutations if mandatory audit is unavailable.
- Correlation and immutable logical records.
- Surface completion-write failures.
- Future append-only/hash-chain/central export designs.

### Denial of service

Mitigations:

- Timeouts, cancellation, bounded output, bounded retries, connection/channel limits.
- Plugin callback exception boundaries.
- Lazy/paged audit queries.
- Startup isolation of corrupt plugins/settings.

## Security invariants for code review

- No secrets in domain records, logs, exception messages, or command arguments.
- No transport call outside approved infrastructure and command pipeline.
- No mutation before audit-start attempt.
- No bypass of host-key verification or execution policy.
- No unbounded output, retry, queue, or background loop.
- No remote output rendered as trusted markup.
- No plugin permission inferred solely from navigation visibility.

## Security review triggers

An explicit security review and usually an ADR are required for:

- New protocol or authentication method.
- Shell-command abstraction expansion.
- Persistent privilege credentials.
- Plugin installation/update/signing.
- Cloud sync or team features.
- AI-generated operations.
- Audit export or encryption.
- Background scheduling or unattended mutation.
- Process isolation/sandboxing.

## Verification

Security-sensitive features require negative tests, redaction tests, malformed-input tests, cancellation/timeouts, and code review against this document.
