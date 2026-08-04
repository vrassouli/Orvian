using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Orvian.Application.Connections;
using Orvian.Application.Contributions;
using Orvian.Application.Execution;
using Orvian.Application.Features;
using Orvian.Application.Files;
using Orvian.Application.Hosts;
using Orvian.Connections;
using Orvian.Discovery;
using Orvian.Diagnostics;
using Orvian.Persistence;
using Orvian.PluginHost;
using Orvian.Security;
using Orvian.Shell;
using Orvian.Ssh;

namespace Orvian.App;

public sealed class OrvianApplication : Avalonia.Application
{
    private CancellationTokenSource? _lifetimeCancellation;
    private SessionCredentialCache? _sessionCredentials;
    private SessionPrivilegeCredentialCache? _privilegeCredentials;
    private ConnectionWorkflow? _connectionWorkflow;
    private PluginHostService? _pluginHost;
    private RollingJsonDiagnosticLog? _diagnosticLog;
    private Task? _initializationTask;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new Style(selector => selector.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(
                    TextBox.VerticalContentAlignmentProperty,
                    VerticalAlignment.Center)
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var databasePath = GetDatabasePath();
            _diagnosticLog = new RollingJsonDiagnosticLog(
                Path.Combine(Path.GetDirectoryName(databasePath)!, "logs"));
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            var database = new OrvianDatabase(databasePath);
            var repository = new SqliteHostProfileRepository(database);
            var discoverySnapshots = new SqliteDiscoverySnapshotRepository(database);
            var secretStore = PlatformSecretStore.Create();
            _sessionCredentials = new SessionCredentialCache();
            _privilegeCredentials = new SessionPrivilegeCredentialCache();
            var hostProfiles = new HostProfileService(repository, secretStore);
            var credentials = new SecretStoreConnectionCredentialProvider(
                secretStore,
                _sessionCredentials);
            var hostKeyDecisions = new InteractiveHostKeyDecisionService();
            var operationConfirmations = new InteractiveOperationConfirmationService();
            var commandTransport = new ConnectionCommandTransport();
            var builtInPermissions = new InMemoryPluginPermissionSource(
            [
                (
                    "orvian.discovery",
                    new Version(0, 1),
                    (IEnumerable<string>)["command.read.execute"])
            ]);
            var auditStore = new SqliteAuditStore(database);
            var trustedHostKeys = new SqliteTrustedHostKeyRepository(database);
            var featureCatalog = new PluginFeatureCatalog();
            var contributionCatalog = new ContributionCatalog();
            var pluginStates = new SqlitePluginStateRepository(database);
            var settings = new SqliteApplicationSettingsRepository(database);
            var auditRetention = new SqliteAuditRetentionService(database);
            _pluginHost = new PluginHostService(
                contributionCatalog,
                new Version(0, 1),
                featureCatalog,
                pluginStates);
            var commandExecutor = new CommandExecutor(
                new PluginCommandPermissionPolicy(
                    new PluginCatalogPermissionSource(
                        featureCatalog,
                        builtInPermissions)),
                auditStore,
                repository,
                commandTransport,
                new CapabilityPrivilegeCommandPreparer(
                    discoverySnapshots,
                    _privilegeCredentials));
            _connectionWorkflow = new ConnectionWorkflow(
                repository,
                trustedHostKeys,
                credentials,
                hostKeyDecisions,
                new SshNetConnectionFactory(),
                commandTransport,
                new HostDiscoveryService(commandExecutor),
                discoverySnapshots);
            var operationCoordinator = new OperationCoordinator(
                commandExecutor,
                auditStore,
                repository,
                operationConfirmations);
            var pluginFeatures = new PluginReadFeatureService(
                featureCatalog,
                discoverySnapshots,
                _connectionWorkflow,
                commandExecutor,
                operationCoordinator);
            var pluginMutations = new PluginMutationFeatureService(
                featureCatalog,
                discoverySnapshots,
                _connectionWorkflow,
                operationCoordinator);
            var localHostFeatures = new PluginLocalHostFeatureService(
                featureCatalog,
                repository,
                discoverySnapshots,
                trustedHostKeys,
                _connectionWorkflow);
            var fileTransfers = new FileTransferService(
                _connectionWorkflow,
                commandTransport,
                repository,
                auditStore,
                auditStore,
                operationConfirmations);
            var fileTransferLocations = new SqliteFileTransferLocationStore(database);
            var viewModel = new MainWindowViewModel(
                repository,
                discoverySnapshots,
                trustedHostKeys,
                hostProfiles,
                _sessionCredentials,
                _connectionWorkflow,
                auditStore,
                pluginFeatures,
                contributionCatalog,
                _privilegeCredentials,
                pluginMutations,
                auditStore,
                localHostFeatures,
                auditStore,
                _pluginHost,
                auditStore,
                settings,
                auditRetention,
                _diagnosticLog,
                _diagnosticLog,
                fileTransfers,
                fileTransferLocations,
                canRememberCredentials:
                    secretStore is not UnavailableSecretStore);
            desktop.MainWindow = new MainWindow(
                viewModel,
                hostKeyDecisions,
                operationConfirmations);
            _lifetimeCancellation = new CancellationTokenSource();
            desktop.Exit += async (_, _) =>
            {
                _lifetimeCancellation.Cancel();
                if (_initializationTask is not null)
                {
                    await _initializationTask;
                    _initializationTask = null;
                }

                _lifetimeCancellation.Dispose();
                _lifetimeCancellation = null;
                _sessionCredentials?.Dispose();
                _sessionCredentials = null;
                _privilegeCredentials?.Dispose();
                _privilegeCredentials = null;
                if (_connectionWorkflow is not null)
                {
                    await _connectionWorkflow.DisposeAsync();
                    _connectionWorkflow = null;
                }

                if (_pluginHost is not null)
                {
                    await _pluginHost.DisposeAsync();
                    _pluginHost = null;
                }

                if (_diagnosticLog is not null)
                {
                    AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                    TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                    await _diagnosticLog.DisposeAsync();
                    _diagnosticLog = null;
                }
            };
            _initializationTask = InitializeLocalStateAsync(
                database,
                viewModel,
                _pluginHost,
                _diagnosticLog,
                _lifetimeCancellation.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }


    private static async Task InitializeLocalStateAsync(
        OrvianDatabase database,
        MainWindowViewModel viewModel,
        PluginHostService pluginHost,
        IApplicationDiagnosticLog diagnosticLog,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        await WriteDiagnosticAsync(
            diagnosticLog,
            correlationId,
            DiagnosticSeverity.Information,
            "startup.started",
            "Application local-state initialization started.",
            CancellationToken.None);
        try
        {
            await database.InitializeAsync(cancellationToken);
            await viewModel.LoadSettingsAsync(cancellationToken);
            var pluginResults = await pluginHost.LoadDirectoryAsync(
                Path.Combine(AppContext.BaseDirectory, "plugins"),
                cancellationToken);
            viewModel.RefreshPluginNavigation();
            await viewModel.LoadHostsAsync(cancellationToken);
            var failedPlugins = pluginResults.Count(result =>
                result.State != PluginState.Active);
            if (failedPlugins > 0)
            {
                foreach (var result in pluginResults.Where(result =>
                             result.State != PluginState.Active))
                {
                    await diagnosticLog.TryWriteAsync(
                        new(
                            Guid.NewGuid(),
                            correlationId,
                            DateTimeOffset.UtcNow,
                            DiagnosticSeverity.Warning,
                            "plugin",
                            "plugin.load_failed",
                            "A discovered plugin did not become active.",
                            System.Collections.Immutable.ImmutableDictionary<string, string>
                                .Empty
                                .Add("plugin_id", result.PluginId)
                                .Add("state", result.State.ToString())
                                .Add(
                                    "diagnostic_codes",
                                    string.Join(
                                        ',',
                                        result.Diagnostics.Select(item => item.Code)))),
                        cancellationToken);
                }

                viewModel.ReportPluginLoadFailure(failedPlugins, correlationId);
            }

            await WriteDiagnosticAsync(
                diagnosticLog,
                correlationId,
                DiagnosticSeverity.Information,
                "startup.completed",
                "Application local-state initialization completed.",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await WriteDiagnosticAsync(
                diagnosticLog,
                correlationId,
                DiagnosticSeverity.Error,
                "startup.failed",
                "Application local-state initialization failed.",
                CancellationToken.None);
            viewModel.ReportInventoryInitializationFailure(correlationId);
        }
    }

    private static Task<bool> WriteDiagnosticAsync(
        IApplicationDiagnosticLog diagnosticLog,
        Guid correlationId,
        DiagnosticSeverity severity,
        string code,
        string safeMessage,
        CancellationToken cancellationToken) =>
        diagnosticLog.TryWriteAsync(
            new(
                Guid.NewGuid(),
                correlationId,
                DateTimeOffset.UtcNow,
                severity,
                "startup",
                code,
                safeMessage,
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty),
            cancellationToken);

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        TryWriteCrashDiagnostic(
            "process.unhandled_exception",
            "An unhandled process exception occurred.",
            DiagnosticSeverity.Critical);
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args)
    {
        TryWriteCrashDiagnostic(
            "task.unobserved_exception",
            "An unobserved background task exception occurred.",
            DiagnosticSeverity.Error);
        args.SetObserved();
    }

    private void TryWriteCrashDiagnostic(
        string code,
        string safeMessage,
        DiagnosticSeverity severity)
    {
        if (_diagnosticLog is null)
        {
            return;
        }

        try
        {
            _diagnosticLog.TryWriteAsync(
                    new(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        DateTimeOffset.UtcNow,
                        severity,
                        "crash",
                        code,
                        safeMessage,
                        System.Collections.Immutable.ImmutableDictionary<string, string>.Empty),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
        }
    }

    private static string GetDatabasePath()
    {
        var applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(applicationData, "Orvian");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "orvian.db");
    }
}
