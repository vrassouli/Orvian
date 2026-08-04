# UI/UX Architecture

## Product experience

Orvian is a system administration application, not an IDE, text editor, terminal multiplexer, or document workspace. Its interaction model should feel closer to macOS System Settings and Windows 11 Settings than to VS Code.

The user should experience Orvian as a calm, structured control center for infrastructure. Technical detail remains available, but the default interface prioritizes understandable states, grouped settings, explicit actions, and predictable navigation.

## Cross-platform identity

Orvian must present one recognizable product identity on macOS, Windows, and Linux.

The following remain consistent across desktop platforms:

- Information architecture.
- Page hierarchy.
- Navigation placement.
- Spacing scale.
- Typography hierarchy.
- Icon language.
- Semantic colors.
- Settings rows and cards.
- Operation and error presentation.
- Plugin contribution placement.

Platform adaptation is limited to native expectations such as:

- Window chrome and title-bar integration.
- System font selection.
- Menu-bar placement on macOS.
- Keyboard shortcuts.
- File and folder pickers.
- Native notifications.
- Light, dark, and system theme integration.
- Scroll and pointer behavior.

Platform-specific styling must not create different product structures or move major features to different places.

## Primary desktop layout

The main desktop window uses a persistent two-column layout:

1. Host sidebar.
2. Settings content area.

The host sidebar contains:

- Search or filtering.
- All hosts.
- Favorites.
- User-defined groups such as Production and Development.
- Network devices and other resource categories when useful.
- Connection state indicators.
- Add Host action.

The content area displays the selected host or application-level settings.

An optional secondary detail pane may be introduced later for wide screens, but it must not be required for basic workflows.

## Navigation hierarchy

The standard hierarchy is:

```text
Host list
  -> Host overview
      -> Feature page
          -> Resource detail
```

Example:

```text
Production Server
  -> Services
      -> nginx
```

Every nested page must provide:

- A visible Back action.
- A breadcrumb when useful on desktop.
- A stable page title.
- A clear host context.

Hosts are not represented as document tabs. The sidebar remains the primary host switcher because Orvian must scale to many managed systems.

## Host overview

The host overview is the landing page after selecting a host. It presents:

- Display name.
- Address or endpoint.
- Connection status.
- Operating system and version.
- Uptime when available.
- CPU, memory, and storage summaries.
- Capability-based feature sections.
- Relevant warnings and pending operations.

Features are grouped as settings categories rather than IDE tool windows.

Recommended default groups:

- System: Overview, Operating System, Date & Time, Network, Storage.
- Administration: Services, Users & Groups, Packages, Firewall.
- Applications: Docker, Podman, Nginx, Databases, and other plugin-provided features.
- Advanced: Terminal, diagnostics, raw command history, and expert tools.

Unavailable features must not appear as broken pages. They may be hidden or shown as unavailable with a precise reason when discoverability is valuable.

## Settings page pattern

Feature pages use grouped settings rows and focused detail pages.

A settings row may contain:

- Label.
- Supporting description.
- Current value or status.
- Toggle.
- Selector.
- Navigation chevron.
- Contextual action.
- Validation or warning text.

Pages should avoid dense dashboards when a settings list communicates the same information more clearly.

Examples include:

- Date & Time with system time, timezone, synchronization toggle, and NTP server.
- Services with searchable service rows, status, enablement, and contextual start, stop, or restart actions.
- Users & Groups with list and detail navigation.
- Docker with image and container tabs, resource rows, contextual lifecycle/log/delete
  actions, an image-pull input, and a visually separate destructive maintenance area.
  Volume, network, and Compose management remain future extensions.
- Container log actions navigate to a container-scoped log page. The MVP uses
  three-second bounded polling through informational audited read operations and
  provides pause, resume, manual refresh, clear, and back controls. Polling stops
  when the page detaches; displayed log text is capped at 128 KiB.
- File Transfer uses equal local/remote panes with path navigation, multi-selection,
  pointer drag and drop, explicit transfer controls, item and aggregate progress,
  cancellation, and a textual status that does not rely on color. Destructive or
  overwrite-capable remote actions use the shared confirmation surface.
  The local pane restores the last valid directory globally; the remote pane restores
  the last valid directory separately for each host and falls back to `/` when that
  location is no longer available.
  A remote file opened locally becomes a watched working copy. When its timestamp or
  size changes, File Transfer offers the normal audited upload/overwrite confirmation
  for the original host and remote directory. Leaving the page or closing Orvian asks
  whether to delete or keep downloaded working copies; cancelling keeps the user on
  the page or prevents window closure.

## Progressive disclosure

Technical detail is never hidden permanently, but it is disclosed progressively.

Default presentation shows:

- Human-readable operation name.
- Current state.
- Expected effect.
- Required privilege.
- Progress and outcome.

Expandable technical detail may show:

- Executable and arguments after redaction.
- Individual command steps.
- Standard output and standard error according to logging policy.
- Exit codes.
- Diagnostic identifiers.

The default user should not need to understand shell syntax to perform a supported action.

## Operations

Simple reversible actions may use inline controls or a lightweight confirmation.

Destructive, privileged, or multi-step actions require an explicit review surface containing:

- Operation title.
- Target host.
- Intended changes.
- Risk or interruption warning.
- Privilege requirement.
- Cancel and Continue actions.

Multi-step operations use a dedicated progress presentation with step states:

- Pending.
- Running.
- Succeeded.
- Failed.
- Skipped.
- Cancelled.

The user must be able to expand command details. Partial completion must be represented accurately and must never be reported as complete success.

## Terminal

Terminal access is an advanced feature, not the product center.

It may be exposed through:

- A host-level Terminal action.
- The Advanced settings group.
- A keyboard shortcut or search result.

Terminal UI must not dominate the default host experience. Supported administration workflows should use structured features and audited commands.

## Search

Global search should find:

- Hosts.
- Settings pages.
- Plugin features.
- Services, containers, and other discovered resources when providers expose searchable metadata.
- Audit operations.
- Commands or actions available to the current context.

Search results must preserve host context and identify the source plugin when relevant.

A command palette may exist as a power-user entry point, but its visual and behavioral role must remain secondary to the Settings-style navigation.

## Notifications and errors

Routine success and informational feedback should use non-blocking notifications.

Errors should appear near the affected content whenever possible and include:

- What failed.
- Which host or resource was affected.
- Whether anything changed.
- A safe recovery action.
- An expandable technical detail section when available.

Modal dialogs are reserved for required decisions, sensitive credential prompts, destructive confirmation, and trust decisions such as unknown or changed host keys.

## Plugin UI contribution model

Plugins extend the Settings-style information architecture. A plugin may contribute:

- Host overview sections.
- Settings groups.
- Settings rows.
- Feature pages.
- Resource list pages.
- Resource detail pages.
- Contextual actions.
- Search providers.
- Notifications.
- Application settings.

Plugins must register declarative metadata and factories through approved abstractions. They must not directly mutate shell controls or assume fixed physical coordinates.

The shell owns:

- Ordering policy.
- Navigation lifecycle.
- Permission and capability filtering.
- Consistent spacing and controls.
- Accessibility.
- Search integration.
- Error boundaries.

Plugin pages must use shared design-system controls unless a specialized visualization is justified and documented.

## Responsive behavior

Desktop layout must respond without becoming a separate product:

- Wide: persistent host sidebar and content; optional detail pane.
- Medium: persistent or collapsible host sidebar and content.
- Narrow desktop: collapsible host sidebar and single content column.

Layout breakpoints are implementation details of the design system and must not be hardcoded independently by plugins.

## Mobile direction

Mobile is a planned presentation client, not part of the initial desktop MVP. Architecture must avoid making mobile reuse impossible.

Target navigation:

```text
Host list
  -> Host overview/settings
      -> Feature page
          -> Resource detail
```

The mobile client should initially prioritize:

- Viewing host health and status.
- Receiving alerts.
- Reviewing operations and audit entries.
- Starting, stopping, or restarting approved services.
- Approving or executing predefined safe actions or runbooks.
- Viewing Docker and service state.

Complex network editing, unrestricted terminal use, and high-risk configuration workflows are not initial mobile priorities.

## Presentation separation

Business and infrastructure behavior must not live in view code.

The intended long-term structure is:

```text
Orvian application/core services
  -> Desktop presentation
  -> Future mobile presentation
  -> Future web presentation, if approved
```

This does not mean sharing every ViewModel unchanged across form factors. It means sharing use cases, contracts, validation, permissions, operation models, and domain behavior while allowing presentation-specific composition.

Views must not call SSH transport, secret stores, or database implementations directly.

## Design system

A shared Orvian design system must define:

- Typography roles.
- Spacing tokens.
- Corner radii.
- Elevation and borders.
- Semantic colors.
- Focus visuals.
- Settings rows.
- Section headers.
- Status badges.
- Host status indicators.
- Buttons and destructive action treatment.
- Empty, loading, unavailable, offline, and error states.
- Operation step controls.

Plugins must consume these tokens and controls. Platform themes may map tokens to native-looking values without changing information architecture.

## Accessibility

All core and plugin UI must support:

- Full keyboard navigation.
- Visible focus states.
- Screen-reader names and relationships.
- Sufficient contrast.
- Status communication that does not rely on color alone.
- Scalable text without clipped actions.
- Reduced-motion preferences where animation exists.

## UX acceptance principles

A UX implementation is acceptable only when:

- The same task is discoverable in the same logical location on macOS, Windows, and Linux.
- The host context is always clear.
- Unsupported capabilities are handled intentionally.
- Privileged and destructive actions state their effect before execution.
- Operation progress and partial failure are truthful.
- Technical detail is available without overwhelming the default interface.
- Plugin pages look and behave like Orvian rather than unrelated embedded applications.
