using Orvian.Shell;
using Orvian.Application.Hosts;
using Orvian.Application.Plugins;
using Orvian.Application.Settings;
using Orvian.Application.Connections;
using Orvian.Application.Discovery;
using Orvian.Connections;
using Orvian.Core.Hosts;
using Orvian.UI.Abstractions;
using Orvian.Security;
using Orvian.Auditing;
using Orvian.Core.Commands;
using Orvian.Discovery;
using Orvian.Diagnostics;
using System.Collections.Immutable;
using Xunit;

namespace Orvian.Shell.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void New_shell_exposes_first_run_empty_state()
    {
        var viewModel = new MainWindowViewModel();

        Assert.Equal(ShellContentState.Empty, viewModel.ContentState);
        Assert.Equal("No hosts yet", viewModel.EmptyStateTitle);
        Assert.Equal("Add Host", viewModel.PrimaryActionTitle);
        Assert.Equal("all-hosts", viewModel.SelectedNavigationItem.Id);
        Assert.False(viewModel.CanToggleConnection);
    }

    [Fact]
    public void Navigation_selection_notifies_the_shell()
    {
        var viewModel = new MainWindowViewModel();
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        var replacement = new ShellNavigationItem(
            "diagnostics",
            "Diagnostics",
            "Show diagnostics");
        viewModel.SelectedNavigationItem = replacement;

        Assert.Equal("diagnostics", viewModel.SelectedNavigationItem.Id);
        Assert.Contains(nameof(MainWindowViewModel.SelectedNavigationItem), changedProperties);
    }

    [Fact]
    public void Plugin_feature_navigation_is_composed_from_catalog()
    {
        var catalog = new FakeContributionCatalog(
            new(
                [
                    new(
                        "orvian.example",
                        "example",
                        "/example",
                        "Example",
                        "Open example",
                        "puzzle",
                        "Example.PageViewModel",
                        [],
                        FeatureId: "example.read")
                ],
                [],
                []));
        var viewModel = new MainWindowViewModel(contributions: catalog);

        viewModel.RefreshPluginNavigation();

        Assert.Collection(
            viewModel.NavigationItems,
            core => Assert.Null(core.FeatureId),
            plugin =>
            {
                Assert.Equal("Example", plugin.Title);
                Assert.Equal("example.read", plugin.FeatureId);
            });
    }

    [Fact]
    public async Task Persisted_hosts_load_into_sidebar_and_select_first()
    {
        var profile = CreateProfile("Production API");
        var viewModel = new MainWindowViewModel(new FakeRepository(profile));

        await viewModel.LoadHostsAsync();

        Assert.Single(viewModel.Hosts);
        Assert.Equal("Production API", viewModel.SelectedHost!.DisplayName);
        Assert.Equal(ShellContentState.Ready, viewModel.ContentState);
        Assert.Equal("Production API", viewModel.ContentTitle);
        Assert.True(viewModel.CanToggleConnection);
    }

    [Fact]
    public async Task Persistence_failure_becomes_safe_error_state()
    {
        var diagnostics = new RecordingDiagnosticLog();
        var viewModel = new MainWindowViewModel(
            new FakeRepository(failure: true),
            diagnosticLog: diagnostics);

        await viewModel.LoadHostsAsync();

        Assert.Equal(ShellContentState.Error, viewModel.ContentState);
        Assert.NotNull(viewModel.SafeErrorMessage);
        Assert.DoesNotContain("exception", viewModel.SafeErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("inventory.load_failed", Assert.Single(diagnostics.Events).Code);
        Assert.Contains(
            diagnostics.Events[0].CorrelationId.ToString("D"),
            viewModel.SafeErrorMessage);
    }

    [Fact]
    public async Task UnavailableSecureStoreSupportsSessionOnlyHostCreation()
    {
        var repository = new FakeRepository();
        using var session = new SessionCredentialCache();
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(
                repository,
                new UnavailableSecretStore()),
            sessionCredentials: session,
            canRememberCredentials: false);

        await viewModel.AddHostAsync(
            "Session host",
            "session.example.test",
            22,
            "operator",
            "session-password".AsMemory(),
            rememberCredential: false);

        var profile = Assert.IsType<HostProfile>(
            await repository.GetAsync(viewModel.SelectedHost!.Id));
        Assert.False(viewModel.CanRememberCredentials);
        Assert.Null(profile.CredentialSecretReference);
        using var credential = session.Retrieve(profile.Id);
        Assert.Equal("session-password", credential!.Memory.ToString());
    }

    [Fact]
    public async Task UnavailableSecureStoreRejectsRememberRequestBeforePersistence()
    {
        var repository = new FakeRepository();
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(
                repository,
                new UnavailableSecretStore()),
            canRememberCredentials: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.AddHostAsync(
                "Persistent host",
                "persistent.example.test",
                22,
                "operator",
                "password".AsMemory(),
                rememberCredential: true));

        Assert.Equal(
            "Persistent credential storage is unavailable.",
            exception.Message);
        Assert.Null(await repository.GetAsync(HostProfileId.New()));
    }

    [Fact]
    public async Task HostSearchIsPassedToBoundedRepositoryQuery()
    {
        var repository = new FakeRepository(CreateProfile("Production API"));
        var viewModel = new MainWindowViewModel(repository)
        {
            SearchText = "production"
        };

        await viewModel.LoadHostsAsync();

        Assert.Equal("production", repository.LastQuery!.SearchText);
        Assert.Equal(500, repository.LastQuery.Limit);
    }

    [Fact]
    public async Task Structured_host_filters_are_parsed_and_bounded()
    {
        var repository = new FakeRepository(CreateProfile("Production API"));
        var viewModel = new MainWindowViewModel(repository)
        {
            SearchText =
                "Production tag:edge os:linux capability:init.systemd " +
                "state:disconnected enabled:true"
        };

        await viewModel.LoadHostsAsync();

        Assert.NotNull(repository.LastQuery);
        Assert.Equal("Production", repository.LastQuery.SearchText);
        Assert.Equal("edge", Assert.Single(repository.LastQuery.Tags));
        Assert.Equal(OperatingSystemFamily.Linux, repository.LastQuery.OperatingSystem);
        Assert.Equal(
            "init.systemd",
            Assert.Single(repository.LastQuery.Capabilities));
        Assert.True(repository.LastQuery.IsEnabled);
        Assert.Single(viewModel.Hosts);
    }

    [Fact]
    public async Task Malformed_structured_host_filter_is_safe_and_skips_repository()
    {
        var repository = new FakeRepository(CreateProfile("Production API"));
        var viewModel = new MainWindowViewModel(repository)
        {
            SearchText = "os:not-a-real-system"
        };

        await viewModel.LoadHostsAsync();

        Assert.Null(repository.LastQuery);
        Assert.Equal(ShellContentState.Error, viewModel.ContentState);
        Assert.Contains(
            "Unknown operating-system filter",
            viewModel.SafeErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteSelectedHostRemovesProfileAndSessionCredential()
    {
        var profile = CreateProfile("Temporary");
        var repository = new FakeRepository(profile);
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        session.Store(profile.Id, "temporary".AsSpan());
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(repository, secrets),
            sessionCredentials: session);
        await viewModel.LoadHostsAsync();

        await viewModel.DeleteSelectedHostAsync();

        Assert.Empty(viewModel.Hosts);
        Assert.Null(session.Retrieve(profile.Id));
    }

    [Fact]
    public async Task UpdateSelectedHostPersistsDetailsAndSessionCredential()
    {
        var profile = CreateProfile("Original");
        var repository = new FakeRepository(profile);
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(repository, secrets),
            sessionCredentials: session);
        await viewModel.LoadHostsAsync();

        await viewModel.UpdateSelectedHostAsync(
            profile,
            "Updated",
            profile.HostName,
            profile.Port,
            profile.UserName,
            "new-session-password".AsMemory(),
            rememberCredential: false,
            profile.AuthenticationMethod,
            profile.PrivateKeyPath,
            tags: ["production", "api"],
            notes: "Primary deployment",
            connectionPreferences: new(
                TimeSpan.FromSeconds(30),
                MaximumReconnectAttempts: 4));

        var updated = await repository.GetAsync(profile.Id);
        Assert.Equal("Updated", updated!.DisplayName);
        Assert.Collection(
            updated.Tags,
            tag => Assert.Equal("api", tag),
            tag => Assert.Equal("production", tag));
        Assert.Equal("Primary deployment", updated.Notes);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            updated.ConnectionPreferences.ConnectionTimeout);
        Assert.Equal(
            4,
            updated.ConnectionPreferences.MaximumReconnectAttempts);
        using var credential = session.Retrieve(profile.Id);
        Assert.Equal("new-session-password", credential!.Memory.ToString());
    }

    [Fact]
    public async Task UpdateCanExplicitlyRemoveRememberedAndSessionCredentials()
    {
        var profile = CreateProfile("Original", "secret-existing");
        var repository = new FakeRepository(profile);
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        session.Store(profile.Id, "session-password".AsSpan());
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(repository, secrets),
            sessionCredentials: session);
        await viewModel.LoadHostsAsync();

        await viewModel.UpdateSelectedHostAsync(
            profile,
            profile.DisplayName,
            profile.HostName,
            profile.Port,
            profile.UserName,
            ReadOnlyMemory<char>.Empty,
            rememberCredential: false,
            profile.AuthenticationMethod,
            profile.PrivateKeyPath,
            removeRememberedCredential: true);

        Assert.Null((await repository.GetAsync(profile.Id))!.CredentialSecretReference);
        Assert.Null(session.Retrieve(profile.Id));
    }

    [Fact]
    public async Task UpdateCanReplaceRememberedCredentialWithSessionOnlyCredential()
    {
        var profile = CreateProfile("Original", "secret-existing");
        var repository = new FakeRepository(profile);
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(repository, secrets),
            sessionCredentials: session);
        await viewModel.LoadHostsAsync();

        await viewModel.UpdateSelectedHostAsync(
            profile,
            profile.DisplayName,
            profile.HostName,
            profile.Port,
            profile.UserName,
            "session-replacement".AsMemory(),
            rememberCredential: false,
            profile.AuthenticationMethod,
            profile.PrivateKeyPath,
            removeRememberedCredential: true);

        Assert.Null((await repository.GetAsync(profile.Id))!.CredentialSecretReference);
        using var replacement = session.Retrieve(profile.Id);
        Assert.Equal("session-replacement", replacement!.Memory.ToString());
    }

    [Fact]
    public async Task Disabling_host_is_durable_clears_session_secrets_and_remains_manageable()
    {
        var profile = CreateProfile("Managed host");
        var repository = new FakeRepository(profile);
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        using var privilege = new SessionPrivilegeCredentialCache();
        session.Store(profile.Id, "ssh-session".AsSpan());
        privilege.Store(profile.Id, "elevation-session".AsSpan());
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(repository, secrets),
            sessionCredentials: session,
            privilegeCredentials: privilege);
        await viewModel.LoadHostsAsync();

        await viewModel.ToggleSelectedHostEnabledAsync();

        Assert.False((await repository.GetAsync(profile.Id))!.IsEnabled);
        Assert.False(viewModel.SelectedHost!.IsEnabled);
        Assert.False(viewModel.CanToggleConnection);
        Assert.True(viewModel.CanManageSelectedHost);
        Assert.Equal("Enable Host", viewModel.HostEnabledActionTitle);
        Assert.Null(session.Retrieve(profile.Id));
        Assert.Null(privilege.Retrieve(profile.Id));

        await viewModel.ToggleSelectedHostEnabledAsync();

        Assert.True((await repository.GetAsync(profile.Id))!.IsEnabled);
        Assert.True(viewModel.CanToggleConnection);
        Assert.Equal("Disable Host", viewModel.HostEnabledActionTitle);
    }

    [Fact]
    public async Task Invalid_connection_preferences_do_not_update_profile()
    {
        var profile = CreateProfile("Managed host");
        var repository = new FakeRepository(profile);
        using var secrets = new InMemorySecretStore();
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(repository, secrets));
        await viewModel.LoadHostsAsync();

        var exception = await Assert.ThrowsAsync<HostProfileValidationException>(
            () => viewModel.UpdateSelectedHostAsync(
                profile,
                profile.DisplayName,
                profile.HostName,
                profile.Port,
                profile.UserName,
                ReadOnlyMemory<char>.Empty,
                rememberCredential: false,
                profile.AuthenticationMethod,
                profile.PrivateKeyPath,
                connectionPreferences: new(
                    TimeSpan.Zero,
                    MaximumReconnectAttempts: 6)));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "connection_timeout.range");
        Assert.Contains(
            exception.Errors,
            error => error.Code == "reconnect_attempts.range");
        Assert.Equal(
            HostConnectionPreferences.Default,
            (await repository.GetAsync(profile.Id))!.ConnectionPreferences);
    }

    [Fact]
    public async Task Active_connection_attempt_changes_action_to_cancel_and_stops_cleanly()
    {
        var profile = CreateProfile("Slow host");
        var repository = new FakeRepository(profile);
        var factory = new BlockingConnectionFactory();
        await using var workflow = new ConnectionWorkflow(
            repository,
            new EmptyTrustedHostKeys(),
            new AvailableCredentials(),
            new RejectHostKeyDecision(),
            factory,
            new ConnectionCommandTransport(),
            new UnusedDiscovery(),
            new EmptyDiscoverySnapshots());
        var viewModel = new MainWindowViewModel(
            repository,
            connections: workflow);
        await viewModel.LoadHostsAsync();

        var connection = viewModel.ToggleSelectedHostConnectionAsync();
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsConnectionAttemptRunning);
        Assert.Equal("Cancel", viewModel.ConnectionActionTitle);
        Assert.False(viewModel.CanManageSelectedHost);
        Assert.Equal(
            ConnectionState.Cancelled,
            await viewModel.ToggleSelectedHostConnectionAsync());
        Assert.Equal(ConnectionState.Cancelled, await connection);
        Assert.False(viewModel.IsConnectionAttemptRunning);
        Assert.Equal("Connect", viewModel.ConnectionActionTitle);
        Assert.True(viewModel.CanManageSelectedHost);
        Assert.Null(viewModel.SafeErrorMessage);
        Assert.Equal(1, factory.CallCount);
    }

    [Fact]
    public async Task Failed_disable_persistence_retains_enabled_state_and_session_secret()
    {
        var profile = CreateProfile("Managed host");
        var repository = new FakeRepository(profile, updateFailure: true);
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        session.Store(profile.Id, "ssh-session".AsSpan());
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(repository, secrets),
            sessionCredentials: session);
        await viewModel.LoadHostsAsync();

        await Assert.ThrowsAsync<IOException>(
            () => viewModel.ToggleSelectedHostEnabledAsync());

        Assert.True((await repository.GetAsync(profile.Id))!.IsEnabled);
        using var retained = session.Retrieve(profile.Id);
        Assert.NotNull(retained);
        Assert.Equal("ssh-session", retained.Memory.ToString());
    }

    [Fact]
    public async Task Duplicating_host_creates_new_profile_without_credential_reference()
    {
        var source = CreateProfile("Production API") with
        {
            CredentialSecretReference = SecretReferenceId.New().Value
        };
        var repository = new CapturingCreateRepository(source);
        using var secrets = new InMemorySecretStore();
        var viewModel = new MainWindowViewModel(
            repository,
            hostProfiles: new HostProfileService(repository, secrets));
        await viewModel.LoadHostsAsync();

        await viewModel.DuplicateSelectedHostAsync();

        var duplicate = Assert.Single(
            repository.Profiles,
            profile => profile.Id != source.Id);
        Assert.Equal("Production API Copy", duplicate.DisplayName);
        Assert.Equal(source.HostName, duplicate.HostName);
        Assert.Equal(source.Tags, duplicate.Tags);
        Assert.Null(duplicate.CredentialSecretReference);
        Assert.Equal(duplicate.Id, viewModel.SelectedHost!.Id);
    }

    [Fact]
    public async Task ActivityFiltersAndCommandDetailsRemainRedacted()
    {
        var audit = new InMemoryAuditStore();
        var included = Guid.NewGuid();
        var startedAt = DateTimeOffset.Parse("2026-07-29T10:00:00Z");
        var start = CommandStart(included, startedAt);
        await audit.CommandStartedAsync(start);
        await audit.CommandCompletedAsync(start with
        {
            CompletedAt = startedAt.AddSeconds(2),
            Status = AuditStatus.Succeeded,
            ExitCode = 0,
            StandardOutputBytes = 12,
            OutputLogging = OutputLoggingMode.Redacted,
            RetainedStandardOutput = "[REDACTED]"
        });
        await audit.CommandStartedAsync(
            CommandStart(Guid.NewGuid(), startedAt.AddDays(-2)) with
            {
                HostProfileId = "other-host"
            });
        var viewModel = new MainWindowViewModel(
            activityReader: audit,
            operationAuditReader: audit,
            commandAuditDetails: audit);

        var activity = await viewModel.LoadActivityAsync(new ActivityFilterViewModel(
            HostProfileId: "host-1",
            PluginId: "orvian.test",
            Source: InvocationSource.UserInterface,
            Status: "Succeeded",
            StartedAtOrAfter: startedAt.AddHours(-1),
            StartedBefore: startedAt.AddHours(1),
            IncludeDiagnostics: true,
            SearchText: "operator test"));
        var item = Assert.Single(activity);
        var detail = await viewModel.LoadCommandAuditDetailAsync(
            item.CommandId!.Value);

        Assert.Equal(included, item.CommandId);
        Assert.NotNull(detail);
        Assert.Contains("[REDACTED]", detail.Content);
        Assert.Contains("Exit code: 0", detail.Content);
        Assert.Contains("Connection: connection-1", detail.Content);
        Assert.Contains("Authenticated user: operator", detail.Content);
        Assert.Contains("Duration: 00:00:02", detail.Content);
        Assert.DoesNotContain(
            "actual-secret",
            detail.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivityIncludesLocalHostTrustEvents()
    {
        var eventId = Guid.NewGuid();
        var reader = new FakeHostTrustAuditReader(new(
            eventId,
            DateTimeOffset.Parse("2026-07-29T10:00:00Z"),
            "host-1",
            HostTrustAction.ReplaceChanged,
            "host.example.test",
            22,
            "192.0.2.10",
            "ssh-ed25519",
            "SHA256:new",
            "SHA256:old"));
        var viewModel = new MainWindowViewModel(hostTrustAuditReader: reader);

        var activity = await viewModel.LoadActivityAsync(new ActivityFilterViewModel(
            HostProfileId: "host-1",
            PluginId: "orvian.core.security",
            Status: "Succeeded",
            SearchText: "host.example"));

        var item = Assert.Single(activity);
        Assert.Equal(eventId, item.OperationId);
        Assert.Equal("Host trust event", item.Kind);
        Assert.Contains("SHA256:new", item.RedactedArguments);
        Assert.Equal("host-1", reader.LastQuery!.HostProfileId);
        Assert.Equal("host.example", reader.LastQuery.SearchText);
    }

    [Fact]
    public async Task ActivityPagesMergeStreamsWithoutDuplicatesOrMissingRows()
    {
        var audit = new InMemoryAuditStore();
        var baseline = DateTimeOffset.Parse("2026-07-29T10:00:00Z");
        for (var index = 0; index < 40; index++)
        {
            await audit.CommandStartedAsync(
                CommandStart(
                    Guid.NewGuid(),
                    baseline.AddSeconds(index * 2)));
            await audit.OperationStartedAsync(
                OperationStartForActivity(
                    Guid.NewGuid(),
                    baseline.AddSeconds((index * 2) + 1)));
        }

        var viewModel = new MainWindowViewModel(
            activityReader: audit,
            operationAuditReader: audit);
        var cursor = new ActivityPageCursor(
            SnapshotBefore: baseline.AddSeconds(100));
        var loaded = new List<ActivityItemViewModel>();
        var pageNumber = 0;
        ActivityPageViewModel page;
        do
        {
            page = await viewModel.LoadActivityPageAsync(
                new ActivityFilterViewModel(),
                cursor,
                pageSize: 25);
            loaded.AddRange(page.Items);
            if (pageNumber++ == 0)
            {
                await audit.CommandStartedAsync(
                    CommandStart(
                        Guid.NewGuid(),
                        baseline.AddSeconds(200)));
            }

            if (page.NextCursor is not null)
            {
                cursor = page.NextCursor;
            }
        }
        while (page.HasMore);

        Assert.Equal(80, loaded.Count);
        Assert.Equal(
            80,
            loaded.Select(item => item.CommandId ?? item.OperationId)
                .Distinct()
                .Count());
        Assert.Equal(
            loaded.OrderByDescending(item => item.StartedAt).Select(item => item.StartedAt),
            loaded.Select(item => item.StartedAt));
        Assert.DoesNotContain(
            loaded,
            item => item.StartedAt >= baseline.AddSeconds(100).ToLocalTime());
    }

    [Fact]
    public async Task ActivityHidesOperationChildCommandsUntilDiagnosticsAreEnabled()
    {
        var audit = new InMemoryAuditStore();
        var operation = OperationStartForActivity(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-07-29T10:00:00Z"));
        await audit.OperationStartedAsync(operation);
        await audit.CommandStartedAsync(
            CommandStart(Guid.NewGuid(), operation.StartedAt) with
            {
                OperationId = operation.OperationId
            });
        var viewModel = new MainWindowViewModel(
            activityReader: audit,
            operationAuditReader: audit);

        var defaultPage = await viewModel.LoadActivityPageAsync(
            new ActivityFilterViewModel(),
            new());
        var diagnosticPage = await viewModel.LoadActivityPageAsync(
            new ActivityFilterViewModel(IncludeDiagnostics: true),
            new());

        Assert.Single(defaultPage.Items);
        Assert.Equal("Operation", defaultPage.Items[0].Kind.Split(' ')[0]);
        Assert.Equal(2, diagnosticPage.Items.Count);
        Assert.Contains(diagnosticPage.Items, item => item.CommandId is not null);
    }

    [Fact]
    public async Task SettingsLoadSaveAndExposeConnectionDefaults()
    {
        var stored = ApplicationSettings.Default with
        {
            Appearance = AppearancePreference.Dark,
            DefaultConnectionTimeout = TimeSpan.FromSeconds(45),
            DefaultMaximumReconnectAttempts = 4
        };
        var repository = new FakeApplicationSettingsRepository(stored);
        var viewModel = new MainWindowViewModel(settingsRepository: repository);
        ApplicationSettings? notified = null;
        viewModel.SettingsChanged += settings => notified = settings;

        await viewModel.LoadSettingsAsync();
        var changed = stored with { AuditRetentionDays = 180 };
        await viewModel.SaveSettingsAsync(changed);

        Assert.True(viewModel.CanManageSettings);
        Assert.Equal(TimeSpan.FromSeconds(45), viewModel.DefaultConnectionPreferences.ConnectionTimeout);
        Assert.Equal(4, viewModel.DefaultConnectionPreferences.MaximumReconnectAttempts);
        Assert.Equal(changed, repository.Value);
        Assert.Equal(changed, notified);
    }

    [Fact]
    public async Task RecoveredSettingsExposeSafeDiagnostic()
    {
        var repository = new FakeApplicationSettingsRepository(
            ApplicationSettings.Default,
            useDefaults: true);
        var viewModel = new MainWindowViewModel(settingsRepository: repository);

        await viewModel.LoadSettingsAsync();

        Assert.Contains("defaults", viewModel.SafeErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetentionCleanupUsesPersistedPolicyAndReportsBoundedResult()
    {
        var retention = new FakeAuditRetentionService();
        var repository = new FakeApplicationSettingsRepository(
            ApplicationSettings.Default with
            {
                OutputRetentionDays = 7,
                AuditRetentionDays = 60
            });
        var viewModel = new MainWindowViewModel(
            settingsRepository: repository,
            auditRetention: retention);
        await viewModel.LoadSettingsAsync();

        var message = await viewModel.RunAuditRetentionAsync();

        Assert.NotNull(retention.Policy);
        Assert.Equal(
            53,
            (retention.Policy.DeleteOutputBefore!.Value -
             retention.Policy.DeleteCompletedBefore).TotalDays);
        Assert.Contains("Cleanup batch complete", message);
    }

    [Fact]
    public async Task DiagnosticsAreExposedOnlyThroughSafeReadModel()
    {
        var correlationId = Guid.NewGuid();
        var reader = new FakeDiagnosticReader(new(
            Guid.NewGuid(),
            correlationId,
            DateTimeOffset.Parse("2026-07-29T10:00:00Z"),
            DiagnosticSeverity.Error,
            "startup",
            "startup.failed",
            "Local initialization failed.",
            ImmutableDictionary<string, string>.Empty.Add("component", "database")));
        var viewModel = new MainWindowViewModel(diagnosticReader: reader);

        var item = Assert.Single(await viewModel.LoadDiagnosticsAsync());

        Assert.True(viewModel.CanViewDiagnostics);
        Assert.Equal(correlationId, item.CorrelationId);
        Assert.Contains("component: database", item.Properties);
        Assert.DoesNotContain("Exception", item.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupFailureShowsMatchingCorrelationId()
    {
        var correlationId = Guid.NewGuid();
        var viewModel = new MainWindowViewModel();

        viewModel.ReportInventoryInitializationFailure(correlationId);

        Assert.Contains(correlationId.ToString("D"), viewModel.SafeErrorMessage);
    }

    [Fact]
    public async Task Plugin_management_is_exposed_and_delegates_state_changes()
    {
        var plugins = new FakePluginManagementService();
        var viewModel = new MainWindowViewModel(pluginManagement: plugins);

        var initial = Assert.Single(await viewModel.GetPluginsAsync());
        var updated = await viewModel.SetPluginEnabledAsync(
            initial.PluginId,
            isEnabled: false);

        Assert.True(viewModel.CanManagePlugins);
        Assert.False(updated.IsEnabled);
        Assert.Equal("orvian.test", plugins.LastChangedPluginId);
    }

    [Fact]
    public async Task Plugin_state_failure_is_safely_logged_and_correlated()
    {
        var plugins = new FakePluginManagementService(failChanges: true);
        var diagnostics = new RecordingDiagnosticLog();
        var viewModel = new MainWindowViewModel(
            pluginManagement: plugins,
            diagnosticLog: diagnostics);

        var exception = await Assert.ThrowsAsync<PluginStateChangeException>(
            () => viewModel.SetPluginEnabledAsync("orvian.test", isEnabled: false));

        var item = Assert.Single(diagnostics.Events);
        Assert.Equal("plugin.state_change_failed", item.Code);
        Assert.Contains(item.CorrelationId.ToString("D"), exception.SafeMessage);
        Assert.DoesNotContain("raw failure", exception.SafeMessage);
    }

    private static CommandAuditEntry CommandStart(
        Guid commandId,
        DateTimeOffset startedAt) =>
        new()
        {
            CommandId = commandId,
            OperationId = Guid.NewGuid(),
            StartedAt = startedAt,
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            RemoteUserName = "operator",
            PluginId = "orvian.test",
            PluginVersion = "0.1.0",
            Executable = "test",
            RedactedArguments = ["--password", "[REDACTED]"],
            InvocationSource = InvocationSource.UserInterface,
            Status = AuditStatus.Started,
            OutputLogging = OutputLoggingMode.Redacted
        };

    private static OperationAuditEntry OperationStartForActivity(
        Guid operationId,
        DateTimeOffset startedAt) =>
        new()
        {
            OperationId = operationId,
            StartedAt = startedAt,
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            RemoteUserName = "operator",
            PluginId = "orvian.test",
            PluginVersion = "0.1.0",
            Title = "Test operation",
            Purpose = "Exercise Activity paging.",
            RequiredPermission = "command.read.execute",
            Risk = OperationRisk.Low,
            Privilege = PrivilegeLevel.User,
            InvocationSource = InvocationSource.UserInterface,
            Status = OperationAuditStatus.Started
        };

    private sealed class FakeHostTrustAuditReader(HostTrustAuditEvent auditEvent)
        : IHostTrustAuditReader
    {
        public HostTrustAuditQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<HostTrustAuditEvent>> QueryHostTrustEventsAsync(
            HostTrustAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<HostTrustAuditEvent>>([auditEvent]);
        }
    }

    private sealed class FakeApplicationSettingsRepository(
        ApplicationSettings value,
        bool useDefaults = false) : IApplicationSettingsRepository
    {
        public ApplicationSettings Value { get; private set; } = value;

        public Task<ApplicationSettingsLoadResult> LoadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationSettingsLoadResult(
                Value,
                useDefaults,
                useDefaults ? "Conservative defaults are active." : null));

        public Task SaveAsync(
            ApplicationSettings settings,
            CancellationToken cancellationToken = default)
        {
            Value = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditRetentionService : IAuditRetentionService
    {
        public AuditRetentionPolicy? Policy { get; private set; }

        public Task<AuditRetentionResult> CleanupBatchAsync(
            AuditRetentionPolicy policy,
            CancellationToken cancellationToken = default)
        {
            Policy = policy;
            return Task.FromResult(new AuditRetentionResult(1, 2, 3, Guid.NewGuid()));
        }
    }

    private sealed class FakeDiagnosticReader(SafeDiagnosticEvent diagnosticEvent)
        : IApplicationDiagnosticReader
    {
        public Task<IReadOnlyList<SafeDiagnosticEvent>> QueryAsync(
            int limit = 200,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SafeDiagnosticEvent>>([diagnosticEvent]);
    }

    private static HostProfile CreateProfile(
        string displayName,
        string? credentialReference = null)
    {
        var now = DateTimeOffset.UtcNow;
        return Assert.IsType<HostProfile>(
            HostProfile.Create(
                HostProfileId.New(),
                displayName,
                "server.example.test",
                22,
                "operator",
                HostAuthenticationMethod.Password,
                credentialReference,
                [],
                null,
                true,
                HostConnectionPreferences.Default,
                now,
                now).Profile);
    }

    private sealed class FakeRepository(
        HostProfile? profile = null,
        bool failure = false,
        bool updateFailure = false) : IHostProfileRepository
    {
        private HostProfile? _profile = profile;

        public HostProfileSearchQuery? LastQuery { get; private set; }

        public Task<HostProfile?> GetAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);

        public Task<HostProfilePage> SearchAsync(
            HostProfileSearchQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return failure
                ? Task.FromException<HostProfilePage>(
                    new IOException("Simulated persistence failure."))
                : Task.FromResult(new HostProfilePage(
                    _profile is null ? [] : [_profile],
                    _profile is null ? 0 : 1,
                    query.Offset,
                    query.Limit));
        }

        public Task AddAsync(
            HostProfile value,
            CancellationToken cancellationToken = default)
        {
            _profile = value;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(
            HostProfile value,
            DateTimeOffset expectedUpdatedAt,
            CancellationToken cancellationToken = default)
        {
            if (updateFailure)
            {
                throw new IOException("Simulated update failure.");
            }

            _profile = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default)
        {
            _profile = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeContributionCatalog(ContributionSnapshot snapshot)
        : IContributionCatalog
    {
        public ContributionSnapshot Snapshot { get; } = snapshot;

        public ContributionRegistrationResult Register(ContributionBatch batch) =>
            throw new NotSupportedException();

        public void RemovePlugin(string pluginId) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingCreateRepository(HostProfile source)
        : IHostProfileRepository
    {
        public List<HostProfile> Profiles { get; } = [source];

        public Task<HostProfile?> GetAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Profiles.SingleOrDefault(profile => profile.Id == id));

        public Task<HostProfilePage> SearchAsync(
            HostProfileSearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostProfilePage(
                [.. Profiles],
                Profiles.Count,
                query.Offset,
                query.Limit));

        public Task AddAsync(
            HostProfile value,
            CancellationToken cancellationToken = default)
        {
            Profiles.Add(value);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(
            HostProfile value,
            DateTimeOffset expectedUpdatedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePluginManagementService(bool failChanges = false)
        : IPluginManagementService
    {
        private PluginManagementItem _item = new(
            "orvian.test",
            "Test plugin",
            "Remotune",
            "0.1.0",
            "Active",
            true,
            ["command.read.execute"],
            []);

        public string? LastChangedPluginId { get; private set; }

        public Task<IReadOnlyList<PluginManagementItem>> GetPluginsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PluginManagementItem>>([_item]);

        public Task<PluginManagementItem> SetEnabledAsync(
            string pluginId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            if (failChanges)
            {
                throw new IOException("raw failure");
            }

            LastChangedPluginId = pluginId;
            _item = _item with
            {
                IsEnabled = isEnabled,
                LifecycleState = isEnabled ? "Active" : "Disabled"
            };
            return Task.FromResult(_item);
        }
    }

    private sealed class RecordingDiagnosticLog : IApplicationDiagnosticLog
    {
        public List<SafeDiagnosticEvent> Events { get; } = [];

        public Task<bool> TryWriteAsync(
            SafeDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(diagnosticEvent);
            return Task.FromResult(true);
        }
    }

    private sealed class BlockingConnectionFactory : IConnectionTransportFactory
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async Task<ConnectionAttemptResult> ConnectAsync(
            ConnectionTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Infinite delay completed unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                return new(ConnectionAttemptStatus.Cancelled);
            }
        }
    }

    private sealed class EmptyTrustedHostKeys : ITrustedHostKeyRepository
    {
        public Task<TrustedHostKey?> GetAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TrustedHostKey?>(null);

        public Task StoreAsync(
            TrustedHostKey trustedHostKey,
            HostTrustAuditEvent auditEvent,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AvailableCredentials : IConnectionCredentialProvider
    {
        public Task<ConnectionAuthentication?> GetAuthenticationAsync(
            HostProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConnectionAuthentication?>(
                new PasswordConnectionAuthentication("test"u8));
    }

    private sealed class RejectHostKeyDecision : IHostKeyDecisionService
    {
        public Task<HostKeyUserDecision> DecideAsync(
            HostKeyVerificationResult verification,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(HostKeyUserDecision.Reject);
    }

    private sealed class UnusedDiscovery : IHostDiscoveryService
    {
        public Task<DiscoverySnapshot> DiscoverAsync(
            DiscoveryContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Discovery should not run.");
    }

    private sealed class EmptyDiscoverySnapshots : IDiscoverySnapshotRepository
    {
        public Task StoreAsync(
            DiscoverySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DiscoverySnapshot?> GetLatestAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DiscoverySnapshot?>(null);
    }
}
