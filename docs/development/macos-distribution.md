# macOS Distribution Strategy

## Decision

Orvian's first supported desktop platform is macOS. Public Apple code signing and notarization are intentionally deferred until the application is functionally complete and ready for external distribution.

The implementation order is:

1. macOS
2. Windows
3. Linux
4. Mobile

This order describes product delivery priority. It does not permit platform-specific code to leak into core application, plugin, command, security, or persistence contracts.

## Current development phase

During active development and internal validation:

- GitHub Actions may build and test macOS artifacts.
- Development artifacts may be unsigned.
- CI must not require Apple Developer credentials.
- No paid Apple account, Developer ID certificate, notarization credential, or signing secret is required for normal pull requests.
- Unsigned artifacts are for maintainers and testers who understand macOS Gatekeeper warnings; they are not considered production releases.
- Signing failures must never block ordinary development until the release-readiness milestone explicitly enables signing.

Recommended development artifacts:

- `Orvian-<version>-macos-arm64.zip`
- `Orvian-<version>-macos-x64.zip`, when Intel support is being tested

Current CI publishes self-contained, single-file, unsigned `Orvian.app` preview
archives for `osx-arm64` and `osx-x64`. These bundles are publish smoke tests and
internal test inputs. They receive only a local ad-hoc signature so the bundle
structure can be validated; they are not Developer ID signed or notarized public
installers.

A DMG may be generated for layout testing, but an unsigned DMG is not a production-quality public installer.

## Paid Apple membership

Developer ID certificates are not purchased individually. They are available through the paid Apple Developer Program membership. Membership, certificate provisioning, and notarization are postponed until Orvian is ready for public beta or stable distribution.

Do not add repository secrets or workflows that assume these credentials exist before that milestone.

## Future release-readiness milestone

When the product is ready for public distribution, create a dedicated release task covering:

- Apple Developer Program enrollment.
- Developer ID Application certificate creation.
- Optional Developer ID Installer certificate if a PKG becomes necessary.
- Hardened Runtime and required entitlements.
- Secure GitHub Actions secret provisioning.
- Recursive signing of the app and all nested native executables/libraries.
- Apple notarization with `notarytool`.
- Stapling and validation.
- Signed and notarized DMG generation.
- SHA-256 checksums and GitHub Release publication.
- Installation tests on clean supported macOS versions.

Signing and notarization are release engineering concerns, not prerequisites for implementing the application architecture or MVP behavior.

## CI/CD stages

### Pull requests and normal branch builds

Required:

1. Restore.
2. Build.
3. Unit, integration, architecture, and UI tests as applicable.
4. Publish unsigned macOS application artifacts when useful.

Forbidden:

- Access to Apple release credentials.
- Notarization.
- Creation of a production release.

### Internal preview tags

Before paid signing is enabled, preview tags may publish clearly labeled unsigned artifacts. Release notes must state that Gatekeeper may block or warn about them.

### Public beta and stable releases

After the release-readiness milestone, tag-triggered workflows may sign, notarize, package, checksum, and publish production macOS releases.

## Packaging direction

The initial public packaging target is a DMG containing `Orvian.app` and an Applications-folder shortcut. A PKG should be introduced only if Orvian later needs to install privileged helpers, daemons, launch agents, or files outside its application bundle.

Separate ARM64 and x64 artifacts are preferred initially. A universal binary may be evaluated later after all native dependencies and update behavior are proven compatible.

## Agent requirements

Agents working on build or release automation must:

- Keep unsigned development CI independent from paid Apple services.
- Never invent or commit certificates, private keys, passwords, API keys, or example secrets that resemble usable credentials.
- Keep signing/notarization steps isolated behind explicit release conditions.
- Preserve deterministic, reproducible application builds before adding signing.
- Treat public release signing as deferred work unless the assigned task explicitly activates the release-readiness milestone.
