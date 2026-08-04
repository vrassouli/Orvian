using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Orvian.Application.Discovery;
using Orvian.Application.Connections;
using Orvian.Auditing;
using Orvian.Application.Hosts;
using Orvian.Application.Features;
using Orvian.Application.Files;
using Orvian.Application.Plugins;
using Orvian.Application.Settings;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Discovery;
using Orvian.Diagnostics;
using Orvian.Security;
using Orvian.UI.Abstractions;

namespace Orvian.Shell;

public sealed record HostListItemViewModel(
    HostProfileId Id,
    string DisplayName,
    string Endpoint,
    bool IsEnabled,
    string? OperatingSystem,
    DateTimeOffset? DiscoveryCompletedAt,
    string? OverviewDetails = null,
    bool IsDiscoveryPartial = false,
    int CapabilityCount = 0,
    Orvian.Connections.ConnectionState ConnectionState =
        Orvian.Connections.ConnectionState.Disconnected)
{
    public string StatusText => IsEnabled ? ConnectionState.ToString() : "Disabled";

    public string CachedDiscoveryText =>
        DiscoveryCompletedAt is null
            ? "No discovery data"
            : $"{OperatingSystem ?? "Unknown OS"} · cached {DiscoveryCompletedAt.Value.ToLocalTime():g} · stale";
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IHostProfileRepository? _hostRepository;
    private readonly IDiscoverySnapshotRepository? _discoverySnapshots;
    private readonly ITrustedHostKeyRepository? _trustedHostKeys;
    private readonly HostProfileService? _hostProfiles;
    private readonly ISessionCredentialCache? _sessionCredentials;
    private readonly IPrivilegeCredentialCache? _privilegeCredentials;
    private readonly ConnectionWorkflow? _connections;
    private readonly IActivityReader? _activityReader;
    private readonly IOperationAuditReader? _operationAuditReader;
    private readonly ICommandAuditDetailReader? _commandAuditDetails;
    private readonly IHostTrustAuditReader? _hostTrustAuditReader;
    private readonly IApplicationSettingsRepository? _settingsRepository;
    private readonly IAuditRetentionService? _auditRetention;
    private readonly IApplicationDiagnosticReader? _diagnosticReader;
    private readonly IApplicationDiagnosticLog? _diagnosticLog;
    private readonly bool _canRememberCredentials;
    private readonly PluginReadFeatureService? _pluginFeatures;
    private readonly PluginLocalHostFeatureService? _localHostFeatures;
    private readonly PluginMutationFeatureService? _pluginMutations;
    private readonly IContributionCatalog? _contributions;
    private readonly IPluginManagementService? _pluginManagement;
    private readonly FileTransferService? _fileTransfers;
    private readonly IFileTransferLocationStore? _fileTransferLocations;
    private ShellNavigationItem _selectedNavigationItem;
    private HostListItemViewModel? _selectedHost;
    private ShellContentState _contentState = ShellContentState.Empty;
    private string? _safeErrorMessage;
    private string? _searchText;
    private readonly object _connectionAttemptGate = new();
    private CancellationTokenSource? _connectionAttemptCancellation;
    private ApplicationSettings _applicationSettings = ApplicationSettings.Default;

    public MainWindowViewModel(
        IHostProfileRepository? hostRepository = null,
        IDiscoverySnapshotRepository? discoverySnapshots = null,
        ITrustedHostKeyRepository? trustedHostKeys = null,
        HostProfileService? hostProfiles = null,
        ISessionCredentialCache? sessionCredentials = null,
        ConnectionWorkflow? connections = null,
        IActivityReader? activityReader = null,
        PluginReadFeatureService? pluginFeatures = null,
        IContributionCatalog? contributions = null,
        IPrivilegeCredentialCache? privilegeCredentials = null,
        PluginMutationFeatureService? pluginMutations = null,
        IOperationAuditReader? operationAuditReader = null,
        PluginLocalHostFeatureService? localHostFeatures = null,
        ICommandAuditDetailReader? commandAuditDetails = null,
        IPluginManagementService? pluginManagement = null,
        IHostTrustAuditReader? hostTrustAuditReader = null,
        IApplicationSettingsRepository? settingsRepository = null,
        IAuditRetentionService? auditRetention = null,
        IApplicationDiagnosticReader? diagnosticReader = null,
        IApplicationDiagnosticLog? diagnosticLog = null,
        FileTransferService? fileTransfers = null,
        IFileTransferLocationStore? fileTransferLocations = null,
        bool canRememberCredentials = true)
    {
        _hostRepository = hostRepository;
        _discoverySnapshots = discoverySnapshots;
        _trustedHostKeys = trustedHostKeys;
        _hostProfiles = hostProfiles;
        _sessionCredentials = sessionCredentials;
        _connections = connections;
        _activityReader = activityReader;
        _pluginFeatures = pluginFeatures;
        _contributions = contributions;
        _privilegeCredentials = privilegeCredentials;
        _pluginMutations = pluginMutations;
        _operationAuditReader = operationAuditReader;
        _localHostFeatures = localHostFeatures;
        _commandAuditDetails = commandAuditDetails;
        _hostTrustAuditReader = hostTrustAuditReader;
        _pluginManagement = pluginManagement;
        _settingsRepository = settingsRepository;
        _auditRetention = auditRetention;
        _diagnosticReader = diagnosticReader;
        _diagnosticLog = diagnosticLog;
        _fileTransfers = fileTransfers;
        _fileTransferLocations = fileTransferLocations;
        _canRememberCredentials = canRememberCredentials;
        NavigationItems.Add(new("all-hosts", "All Hosts", "Show all hosts"));

        _selectedNavigationItem = NavigationItems[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<ApplicationSettings>? SettingsChanged;

    public string ProductName => "Remotune";

    public bool CanRememberCredentials => _canRememberCredentials;

    public string HostSearchWatermark =>
        "Search or filter with tag:, os:, capability:, state:, enabled:";

    public string EmptyStateTitle => "No hosts yet";

    public string EmptyStateDescription =>
        "Add a host to securely connect, discover its capabilities, and manage supported features.";

    public string PrimaryActionTitle => "Add Host";

    public string ActivityTitle => "Activity";

    public string SettingsTitle => "Settings";

    public bool CanManagePlugins => _pluginManagement is not null;

    public bool CanManageSettings => _settingsRepository is not null;

    public bool CanRunAuditRetention => _auditRetention is not null;

    public bool CanViewDiagnostics => _diagnosticReader is not null;

    public bool CanTransferFiles => _fileTransfers is not null;

    public FileTransferService FileTransfers => _fileTransfers ??
        throw new InvalidOperationException("File transfer is unavailable.");

    public IFileTransferLocationStore? FileTransferLocations =>
        _fileTransferLocations;

    public async Task<IReadOnlyList<DiagnosticItemViewModel>> LoadDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_diagnosticReader is null)
        {
            return [];
        }

        return (await _diagnosticReader.QueryAsync(
                limit: 300,
                cancellationToken))
            .Select(item => new DiagnosticItemViewModel(
                item.OccurredAt.ToLocalTime(),
                item.Severity.ToString(),
                item.Category,
                item.Code,
                item.SafeMessage,
                item.CorrelationId,
                string.Join(
                    Environment.NewLine,
                    item.SafeProperties
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}: {pair.Value}"))))
            .ToArray();
    }

    public ApplicationSettings ApplicationSettings => _applicationSettings;

    public HostConnectionPreferences DefaultConnectionPreferences =>
        new(
            _applicationSettings.DefaultConnectionTimeout,
            _applicationSettings.DefaultMaximumReconnectAttempts);

    public async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_settingsRepository is null)
        {
            return;
        }

        var result = await _settingsRepository.LoadAsync(cancellationToken);
        _applicationSettings = result.Settings;
        SettingsChanged?.Invoke(_applicationSettings);
        if (result.UsedDefaults)
        {
            SafeErrorMessage = result.SafeDiagnostic;
        }
    }

    public async Task SaveSettingsAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (_settingsRepository is null)
        {
            throw new InvalidOperationException("Application settings are unavailable.");
        }

        ApplicationSettings.Validate(settings);
        await _settingsRepository.SaveAsync(settings, cancellationToken);
        _applicationSettings = settings;
        SettingsChanged?.Invoke(settings);
        SafeErrorMessage = null;
    }

    public async Task<string> RunAuditRetentionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_auditRetention is null)
        {
            throw new InvalidOperationException("Audit retention is unavailable.");
        }

        var now = DateTimeOffset.UtcNow;
        var result = await _auditRetention.CleanupBatchAsync(
            new(
                now.AddDays(-_applicationSettings.AuditRetentionDays),
                now.AddDays(-_applicationSettings.OutputRetentionDays)),
            cancellationToken);
        return
            $"Cleanup batch complete: {result.DeletedOperations} operations, " +
            $"{result.DeletedCommands} commands deleted; " +
            $"{result.PurgedCommandOutputs} retained outputs purged.";
    }

    public Task<IReadOnlyList<PluginManagementItem>> GetPluginsAsync(
        CancellationToken cancellationToken = default) =>
        _pluginManagement?.GetPluginsAsync(cancellationToken) ??
        Task.FromResult<IReadOnlyList<PluginManagementItem>>([]);

    public async Task<PluginManagementItem> SetPluginEnabledAsync(
        string pluginId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (_pluginManagement is null)
        {
            throw new InvalidOperationException("Plugin management is unavailable.");
        }

        try
        {
            var result = await _pluginManagement.SetEnabledAsync(
                pluginId,
                isEnabled,
                cancellationToken);
            RefreshPluginNavigation();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var correlationId = Guid.NewGuid();
            await TryWriteDiagnosticAsync(
                correlationId,
                DiagnosticSeverity.Error,
                "plugin",
                "plugin.state_change_failed",
                "A plugin state change failed.",
                ImmutableDictionary<string, string>.Empty
                    .Add("plugin_id", pluginId)
                    .Add("requested_state", isEnabled ? "enabled" : "disabled"));
            var safeMessage = exception is PluginStateChangeException known
                ? known.SafeMessage
                : "The plugin state could not be changed safely.";
            throw new PluginStateChangeException(
                $"{safeMessage} Correlation: {correlationId:D}.");
        }
    }

    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal))
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
        }
    }

    public string ConnectionActionTitle =>
        IsConnectionAttemptRunning
            ? "Cancel"
            : SelectedHost?.ConnectionState is (
                Orvian.Connections.ConnectionState.Connected or
                Orvian.Connections.ConnectionState.Discovering or
                Orvian.Connections.ConnectionState.Ready)
            ? "Disconnect"
            : "Connect";

    public bool IsConnectionAttemptRunning
    {
        get
        {
            lock (_connectionAttemptGate)
            {
                return _connectionAttemptCancellation is not null;
            }
        }
    }

    public bool CanToggleConnection =>
        SelectedHost is not null &&
        SelectedHost.IsEnabled;

    public bool CanManageSelectedHost =>
        SelectedHost is not null && !IsConnectionAttemptRunning;

    public string HostEnabledActionTitle =>
        SelectedHost?.IsEnabled == true ? "Disable Host" : "Enable Host";

    public bool CanRefreshDiscovery =>
        SelectedHost?.ConnectionState is (
            Orvian.Connections.ConnectionState.Ready or
            Orvian.Connections.ConnectionState.Connected);

    public bool CanLoadFeatures => CanRefreshDiscovery && _pluginFeatures is not null;

    public bool CanSetPrivilegeCredential =>
        CanRefreshDiscovery && _privilegeCredentials is not null;

    public string ContentTitle =>
        SelectedHost?.DisplayName ??
        (ContentState == ShellContentState.Error ? "Host inventory unavailable" : EmptyStateTitle);

    public string ContentSubtitle =>
        SelectedHost is null
            ? string.Empty
            : $"{SelectedHost.Endpoint}  ·  {SelectedHost.StatusText}  ·  " +
              SelectedHost.CachedDiscoveryText;

    public string ContentDetails =>
        SelectedHost?.OverviewDetails ?? SafeErrorMessage ?? EmptyStateDescription;

    public bool ShowPrimaryAddHost => SelectedHost is null;

    public string ContentDescription =>
        SelectedHost is not null
            ? $"{SelectedHost.Endpoint} · {SelectedHost.StatusText}\n" +
              $"{SelectedHost.CachedDiscoveryText}" +
              (SelectedHost.OverviewDetails is null
                  ? string.Empty
                  : $"\n\n{SelectedHost.OverviewDetails}") +
              (SafeErrorMessage is null ? string.Empty : $"\n{SafeErrorMessage}")
            : SafeErrorMessage ?? EmptyStateDescription;

    public string? SafeErrorMessage
    {
        get => _safeErrorMessage;
        private set
        {
            if (_safeErrorMessage == value)
            {
                return;
            }

            _safeErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContentDescription));
            OnPropertyChanged(nameof(ContentDetails));
            OnPropertyChanged(nameof(ConnectionActionTitle));
            OnPropertyChanged(nameof(CanToggleConnection));
            OnPropertyChanged(nameof(CanRefreshDiscovery));
            OnPropertyChanged(nameof(CanLoadFeatures));
        }
    }

    public ShellContentState ContentState
    {
        get => _contentState;
        private set
        {
            if (_contentState == value)
            {
                return;
            }

            _contentState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContentTitle));
        }
    }

    public ObservableCollection<ShellNavigationItem> NavigationItems { get; } = [];

    public ObservableCollection<HostListItemViewModel> Hosts { get; } = [];

    public ShellNavigationItem SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selectedNavigationItem == value)
            {
                return;
            }

            _selectedNavigationItem = value;
            OnPropertyChanged();
        }
    }

    public HostListItemViewModel? SelectedHost
    {
        get => _selectedHost;
        set
        {
            if (_selectedHost == value)
            {
                return;
            }

            _selectedHost = value;
            ContentState = value is null
                ? Hosts.Count == 0 ? ShellContentState.Empty : ShellContentState.Ready
                : ShellContentState.Ready;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContentTitle));
            OnPropertyChanged(nameof(ContentDescription));
            OnPropertyChanged(nameof(ContentSubtitle));
            OnPropertyChanged(nameof(ContentDetails));
            OnPropertyChanged(nameof(ShowPrimaryAddHost));
            OnPropertyChanged(nameof(ConnectionActionTitle));
            OnPropertyChanged(nameof(CanToggleConnection));
            OnPropertyChanged(nameof(CanManageSelectedHost));
            OnPropertyChanged(nameof(HostEnabledActionTitle));
            OnPropertyChanged(nameof(CanRefreshDiscovery));
            OnPropertyChanged(nameof(CanLoadFeatures));
            OnPropertyChanged(nameof(CanSetPrivilegeCredential));
        }
    }

    public async Task LoadHostsAsync(CancellationToken cancellationToken = default)
    {
        if (_hostRepository is null)
        {
            return;
        }

        ContentState = ShellContentState.Loading;
        SafeErrorMessage = null;
        try
        {
            if (!TryParseHostFilter(SearchText, out var filter, out var filterError))
            {
                Hosts.Clear();
                SelectedHost = null;
                SafeErrorMessage = filterError;
                ContentState = ShellContentState.Error;
                return;
            }

            var page = await _hostRepository.SearchAsync(
                new(
                    filter.SearchText,
                    filter.Tags,
                    IncludeDisabled: true,
                    Offset: 0,
                    Limit: 500,
                    filter.OperatingSystem,
                    filter.Capabilities,
                    filter.IsEnabled),
                cancellationToken);
            Hosts.Clear();
            foreach (var host in page.Items)
            {
                var connectionState = _connections?.GetSnapshot(host.Id).State ??
                    Orvian.Connections.ConnectionState.Disconnected;
                if (filter.ConnectionState is not null &&
                    connectionState != filter.ConnectionState)
                {
                    continue;
                }

                var discovery = _discoverySnapshots is null
                    ? null
                    : await _discoverySnapshots.GetLatestAsync(host.Id, cancellationToken);
                var trustedKey = _trustedHostKeys is null
                    ? null
                    : await _trustedHostKeys.GetAsync(host.Id, cancellationToken);
                Hosts.Add(new(
                    host.Id,
                    host.DisplayName,
                    $"{host.HostName}:{host.Port}",
                    host.IsEnabled,
                    discovery?.OperatingSystem.ToString(),
                    discovery?.CompletedAt,
                    BuildOverview(discovery, trustedKey),
                    discovery?.IsPartial ?? false,
                    discovery?.Capabilities.Count ?? 0,
                    connectionState));
            }

            SelectedHost = Hosts.FirstOrDefault();
            ContentState = Hosts.Count == 0 ? ShellContentState.Empty : ShellContentState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ContentState = ShellContentState.Empty;
        }
        catch (Exception)
        {
            var correlationId = Guid.NewGuid();
            await TryWriteDiagnosticAsync(
                correlationId,
                DiagnosticSeverity.Error,
                "inventory",
                "inventory.load_failed",
                "The local host inventory could not be loaded.",
                ImmutableDictionary<string, string>.Empty);
            SafeErrorMessage =
                "Remotune could not load the local host inventory. Restart the application or inspect diagnostics." +
                FormatCorrelation(correlationId);
            ContentState = ShellContentState.Error;
        }
    }

    private async Task TryWriteDiagnosticAsync(
        Guid correlationId,
        DiagnosticSeverity severity,
        string category,
        string code,
        string safeMessage,
        ImmutableDictionary<string, string> safeProperties)
    {
        if (_diagnosticLog is null)
        {
            return;
        }

        try
        {
            await _diagnosticLog.TryWriteAsync(
                new(
                    Guid.NewGuid(),
                    correlationId,
                    DateTimeOffset.UtcNow,
                    severity,
                    category,
                    code,
                    safeMessage,
                    safeProperties));
        }
        catch (Exception)
        {
        }
    }

    private static bool TryParseHostFilter(
        string? input,
        out ParsedHostFilter filter,
        out string? safeError)
    {
        var textTerms = new List<string>();
        var tags = ImmutableArray.CreateBuilder<string>();
        var capabilities = ImmutableArray.CreateBuilder<string>();
        OperatingSystemFamily? operatingSystem = null;
        Orvian.Connections.ConnectionState? connectionState = null;
        bool? isEnabled = null;

        foreach (var token in (input ?? string.Empty).Split(
                     ' ',
                     StringSplitOptions.TrimEntries |
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf(':');
            if (separator <= 0)
            {
                textTerms.Add(token);
                continue;
            }

            var key = token[..separator].ToLowerInvariant();
            var value = token[(separator + 1)..].Trim();
            if (key is not ("tag" or "os" or "capability" or "state" or "enabled"))
            {
                textTerms.Add(token);
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                filter = default!;
                safeError = $"Filter '{key}:' requires a value.";
                return false;
            }

            switch (key)
            {
                case "tag":
                    if (value.Length > 100 || tags.Count >= 64)
                    {
                        filter = default!;
                        safeError = "Tag filters must contain at most 64 values of 100 characters.";
                        return false;
                    }

                    tags.Add(value);
                    break;
                case "capability":
                    if (value.Length > 200 ||
                        capabilities.Count >= 64 ||
                        value.Any(character =>
                            !(char.IsAsciiLetterOrDigit(character) ||
                              character is '.' or '-' or '_')))
                    {
                        filter = default!;
                        safeError = "Capability filters must use a bounded dotted identifier.";
                        return false;
                    }

                    capabilities.Add(value.ToLowerInvariant());
                    break;
                case "os":
                    if (!Enum.TryParse<OperatingSystemFamily>(
                            value,
                            ignoreCase: true,
                            out var parsedOperatingSystem) ||
                        !Enum.IsDefined(parsedOperatingSystem))
                    {
                        filter = default!;
                        safeError = $"Unknown operating-system filter '{value}'.";
                        return false;
                    }

                    operatingSystem = parsedOperatingSystem;
                    break;
                case "state":
                    if (!Enum.TryParse<Orvian.Connections.ConnectionState>(
                            value,
                            ignoreCase: true,
                            out var parsedConnectionState) ||
                        !Enum.IsDefined(parsedConnectionState))
                    {
                        filter = default!;
                        safeError = $"Unknown connection-state filter '{value}'.";
                        return false;
                    }

                    connectionState = parsedConnectionState;
                    break;
                case "enabled":
                    if (!bool.TryParse(value, out var parsedEnabled))
                    {
                        filter = default!;
                        safeError = "The enabled filter accepts only true or false.";
                        return false;
                    }

                    isEnabled = parsedEnabled;
                    break;
            }
        }

        filter = new(
            textTerms.Count == 0 ? null : string.Join(' ', textTerms),
            tags.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            operatingSystem,
            capabilities.Distinct(StringComparer.Ordinal).ToImmutableArray(),
            connectionState,
            isEnabled);
        safeError = null;
        return true;
    }

    private sealed record ParsedHostFilter(
        string? SearchText,
        ImmutableArray<string> Tags,
        OperatingSystemFamily? OperatingSystem,
        ImmutableArray<string> Capabilities,
        Orvian.Connections.ConnectionState? ConnectionState,
        bool? IsEnabled);

    public void ReportInventoryInitializationFailure(Guid? correlationId = null)
    {
        SafeErrorMessage =
            "Remotune could not initialize local storage. Restart the application or inspect diagnostics." +
            FormatCorrelation(correlationId);
        ContentState = ShellContentState.Error;
    }

    public void ReportPluginLoadFailure(int count, Guid? correlationId = null)
    {
        SafeErrorMessage =
            $"{count} plugin{(count == 1 ? string.Empty : "s")} could not be loaded. " +
            "Open diagnostics before using affected features." +
            FormatCorrelation(correlationId);
    }

    private static string FormatCorrelation(Guid? correlationId) =>
        correlationId is null
            ? string.Empty
            : $" Correlation: {correlationId.Value:D}.";

    public async Task AddHostAsync(
        string displayName,
        string hostName,
        int port,
        string userName,
        ReadOnlyMemory<char> password,
        bool rememberCredential,
        HostAuthenticationMethod authenticationMethod = HostAuthenticationMethod.Password,
        string? privateKeyPath = null,
        ImmutableArray<string> tags = default,
        string? notes = null,
        HostConnectionPreferences? connectionPreferences = null,
        CancellationToken cancellationToken = default)
    {
        if (_hostProfiles is null)
        {
            throw new InvalidOperationException("Host profile editing is unavailable.");
        }

        if (rememberCredential && !_canRememberCredentials)
        {
            throw new InvalidOperationException(
                "Persistent credential storage is unavailable.");
        }

        using var credential = password.IsEmpty ? null : new SecretValue(password.Span);
        var input = new HostProfileInput(
            displayName,
            hostName,
            port,
            userName,
            authenticationMethod,
            tags.IsDefault ? [] : tags,
            notes,
            IsEnabled: true,
            connectionPreferences ?? HostConnectionPreferences.Default,
            privateKeyPath);
        var result = await _hostProfiles.CreateAsync(
            new(input, rememberCredential ? credential : null),
            cancellationToken);

        if (!rememberCredential && credential is not null && _sessionCredentials is not null)
        {
            _sessionCredentials.Store(result.Profile!.Id, credential.Memory.Span);
        }

        await LoadHostsAsync(cancellationToken);
        SelectedHost = Hosts.FirstOrDefault(host => host.Id == result.Profile!.Id);
    }

    public Task<HostProfile?> GetSelectedHostProfileAsync(
        CancellationToken cancellationToken = default) =>
        SelectedHost is null || _hostRepository is null
            ? Task.FromResult<HostProfile?>(null)
            : _hostRepository.GetAsync(SelectedHost.Id, cancellationToken);

    public async Task UpdateSelectedHostAsync(
        HostProfile existing,
        string displayName,
        string hostName,
        int port,
        string userName,
        ReadOnlyMemory<char> credentialValue,
        bool rememberCredential,
        HostAuthenticationMethod authenticationMethod,
        string? privateKeyPath,
        ImmutableArray<string> tags = default,
        string? notes = null,
        HostConnectionPreferences? connectionPreferences = null,
        bool removeRememberedCredential = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (_hostProfiles is null)
        {
            throw new InvalidOperationException("Host profile editing is unavailable.");
        }

        if (rememberCredential && !_canRememberCredentials)
        {
            throw new InvalidOperationException(
                "Persistent credential storage is unavailable.");
        }

        if (_connections is not null &&
            _connections.GetSnapshot(existing.Id).State !=
                Orvian.Connections.ConnectionState.Disconnected)
        {
            await _connections.DisconnectAsync(existing.Id, cancellationToken);
            _privilegeCredentials?.Delete(existing.Id);
        }

        using var credential = credentialValue.IsEmpty
            ? null
            : new SecretValue(credentialValue.Span);
        var input = new HostProfileInput(
            displayName,
            hostName,
            port,
            userName,
            authenticationMethod,
            tags.IsDefault ? existing.Tags : tags,
            notes ?? existing.Notes,
            existing.IsEnabled,
            connectionPreferences ?? existing.ConnectionPreferences,
            privateKeyPath);
        var result = await _hostProfiles.UpdateAsync(new(
            existing.Id,
            existing.UpdatedAt,
            input,
            rememberCredential ? credential : null,
            removeRememberedCredential),
            cancellationToken);
        if (_sessionCredentials is not null)
        {
            if (!rememberCredential && credential is not null)
            {
                _sessionCredentials.Store(existing.Id, credential.Memory.Span);
            }
            else if (rememberCredential ||
                     removeRememberedCredential ||
                     authenticationMethod != existing.AuthenticationMethod)
            {
                _sessionCredentials.Delete(existing.Id);
            }
        }

        await LoadHostsAsync(cancellationToken);
        SelectedHost = Hosts.FirstOrDefault(host => host.Id == result.Profile!.Id);
        if (result.Warnings.Length > 0)
        {
            SafeErrorMessage = string.Join(
                Environment.NewLine,
                result.Warnings.Select(warning => warning.SafeMessage));
        }
    }

    public async Task<Orvian.Connections.ConnectionState?> ToggleSelectedHostConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_connections is null || SelectedHost is null)
        {
            return null;
        }

        CancellationTokenSource? activeAttempt;
        lock (_connectionAttemptGate)
        {
            activeAttempt = _connectionAttemptCancellation;
        }

        if (activeAttempt is not null)
        {
            TryCancel(activeAttempt);
            return Orvian.Connections.ConnectionState.Cancelled;
        }

        var current = _connections.GetSnapshot(SelectedHost.Id);
        if (current.IsConnected)
        {
            await _connections.DisconnectAsync(SelectedHost.Id, cancellationToken);
            _privilegeCredentials?.Delete(SelectedHost.Id);
            if (_applicationSettings.ClearSessionCredentialsOnDisconnect)
            {
                _sessionCredentials?.Delete(SelectedHost.Id);
            }

            await LoadHostsAsync(cancellationToken);
            return Orvian.Connections.ConnectionState.Disconnected;
        }

        if (current.State != Orvian.Connections.ConnectionState.Disconnected)
        {
            await _connections.DisconnectAsync(SelectedHost.Id, cancellationToken);
            _privilegeCredentials?.Delete(SelectedHost.Id);
            if (_applicationSettings.ClearSessionCredentialsOnDisconnect)
            {
                _sessionCredentials?.Delete(SelectedHost.Id);
            }
        }

        SafeErrorMessage = null;
        using var attemptCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_connectionAttemptGate)
        {
            _connectionAttemptCancellation = attemptCancellation;
        }

        NotifyConnectionActionChanged();
        try
        {
            var result = await _connections.ConnectAsync(
                SelectedHost.Id,
                attemptCancellation.Token);
            var safeFailure = result.Snapshot.State is not (
                Orvian.Connections.ConnectionState.Ready or
                Orvian.Connections.ConnectionState.Connected or
                Orvian.Connections.ConnectionState.Cancelled)
                ? result.Snapshot.SafeFailureMessage ??
                    "The host connection did not become ready."
                : result.SafeFailureMessage;
            if (!cancellationToken.IsCancellationRequested)
            {
                await LoadHostsAsync(cancellationToken);
                SelectedHost = Hosts.FirstOrDefault(
                    host => host.Id == result.Snapshot.HostProfileId);
            }

            SafeErrorMessage = safeFailure;
            return result.Snapshot.State;
        }
        finally
        {
            lock (_connectionAttemptGate)
            {
                if (ReferenceEquals(
                        _connectionAttemptCancellation,
                        attemptCancellation))
                {
                    _connectionAttemptCancellation = null;
                }
            }

            NotifyConnectionActionChanged();
        }
    }

    public void CancelConnectionAttempt()
    {
        CancellationTokenSource? activeAttempt;
        lock (_connectionAttemptGate)
        {
            activeAttempt = _connectionAttemptCancellation;
        }

        if (activeAttempt is not null)
        {
            TryCancel(activeAttempt);
        }
    }

    public async Task ToggleSelectedHostEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedHost is null || _hostProfiles is null || _hostRepository is null)
        {
            return;
        }

        var hostId = SelectedHost.Id;
        var existing = await _hostRepository.GetAsync(hostId, cancellationToken)
            ?? throw new InvalidOperationException("Host profile no longer exists.");
        var isEnabling = !existing.IsEnabled;
        if (!isEnabling &&
            _connections is not null &&
            _connections.GetSnapshot(hostId).State !=
                Orvian.Connections.ConnectionState.Disconnected)
        {
            await _connections.DisconnectAsync(hostId, cancellationToken);
        }

        var input = new HostProfileInput(
            existing.DisplayName,
            existing.HostName,
            existing.Port,
            existing.UserName,
            existing.AuthenticationMethod,
            existing.Tags,
            existing.Notes,
            isEnabling,
            existing.ConnectionPreferences,
            existing.PrivateKeyPath);
        var result = await _hostProfiles.UpdateAsync(
            new(existing.Id, existing.UpdatedAt, input),
            cancellationToken);
        if (!isEnabling)
        {
            _sessionCredentials?.Delete(hostId);
            _privilegeCredentials?.Delete(hostId);
        }

        await LoadHostsAsync(cancellationToken);
        SelectedHost = Hosts.FirstOrDefault(host => host.Id == result.Profile!.Id);
        SafeErrorMessage = result.Warnings.IsDefaultOrEmpty
            ? null
            : string.Join(
                Environment.NewLine,
                result.Warnings.Select(warning => warning.SafeMessage));
    }

    public async Task DuplicateSelectedHostAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedHost is null || _hostProfiles is null || _hostRepository is null)
        {
            return;
        }

        var existing = await _hostRepository.GetAsync(
            SelectedHost.Id,
            cancellationToken) ??
            throw new InvalidOperationException("Host profile no longer exists.");
        var input = new HostProfileInput(
            $"{existing.DisplayName} Copy",
            existing.HostName,
            existing.Port,
            existing.UserName,
            existing.AuthenticationMethod,
            existing.Tags,
            existing.Notes,
            existing.IsEnabled,
            existing.ConnectionPreferences,
            existing.PrivateKeyPath);
        var result = await _hostProfiles.CreateAsync(
            new(input),
            cancellationToken);

        await LoadHostsAsync(cancellationToken);
        SelectedHost = Hosts.FirstOrDefault(host => host.Id == result.Profile!.Id);
    }

    public void SetSelectedHostSessionCredential(ReadOnlySpan<char> credential)
    {
        if (SelectedHost is null || _sessionCredentials is null)
        {
            throw new InvalidOperationException("No host is selected for a session credential.");
        }

        _sessionCredentials.Store(SelectedHost.Id, credential);
    }

    public void SetSelectedHostPrivilegeCredential(ReadOnlySpan<char> credential)
    {
        if (SelectedHost is null || _privilegeCredentials is null)
        {
            throw new InvalidOperationException(
                "No host is selected for a privilege credential.");
        }

        _privilegeCredentials.Store(SelectedHost.Id, credential);
    }

    public async Task DeleteSelectedHostAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedHost is null || _hostProfiles is null)
        {
            return;
        }

        var hostId = SelectedHost.Id;
        if (_connections is not null &&
            _connections.GetSnapshot(hostId).State !=
                Orvian.Connections.ConnectionState.Disconnected)
        {
            await _connections.DisconnectAsync(hostId, cancellationToken);
        }

        var result = await _hostProfiles.DeleteAsync(hostId, cancellationToken);
        _sessionCredentials?.Delete(hostId);
        _privilegeCredentials?.Delete(hostId);
        await LoadHostsAsync(cancellationToken);
        if (result.Warnings.Length > 0)
        {
            SafeErrorMessage = string.Join(
                Environment.NewLine,
                result.Warnings.Select(warning => warning.SafeMessage));
        }
    }

    public async Task RefreshSelectedHostDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        if (_connections is null || SelectedHost is null)
        {
            return;
        }

        var hostId = SelectedHost.Id;
        var result = await _connections.RefreshDiscoveryAsync(hostId, cancellationToken);
        await LoadHostsAsync(cancellationToken);
        SelectedHost = Hosts.FirstOrDefault(host => host.Id == hostId);
        SafeErrorMessage = result.SafeFailureMessage;
    }

    public void RefreshPluginNavigation()
    {
        var selectedId = SelectedNavigationItem.Id;
        while (NavigationItems.Count > 1)
        {
            NavigationItems.RemoveAt(NavigationItems.Count - 1);
        }

        if (_contributions is not null)
        {
            foreach (var page in _contributions.Snapshot.NavigationPages
                         .Where(page => page.FeatureId is not null)
                         .OrderBy(page => page.SortOrder)
                         .ThenBy(page => page.Title, StringComparer.Ordinal))
            {
                NavigationItems.Add(new(
                    page.Id,
                    page.Title,
                    page.AccessibleName,
                    page.FeatureId));
            }
        }

        SelectedNavigationItem =
            NavigationItems.FirstOrDefault(item =>
                string.Equals(item.Id, selectedId, StringComparison.Ordinal)) ??
            NavigationItems[0];
    }

    public async Task<PluginFeatureDisplay> LoadFeatureAsync(
        string featureId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (SelectedHost is null)
        {
            return new(
                title,
                "Select a host before loading this feature.",
                IsError: true);
        }

        PluginFeatureReadResult result;
        if (_localHostFeatures?.Supports(featureId) == true)
        {
            result = await _localHostFeatures.ReadAsync(
                featureId,
                SelectedHost.Id,
                cancellationToken);
        }
        else if (_pluginFeatures is not null)
        {
            result = await _pluginFeatures.ReadAsync(
                featureId,
                SelectedHost.Id,
                cancellationToken);
        }
        else
        {
            return new(
                title,
                "Feature loading is unavailable.",
                IsError: true);
        }
        if (!result.IsSuccess || result.Model is null)
        {
            return new(
                title,
                result.SafeFailureMessage ??
                    "Feature information could not be loaded.",
                IsError: true);
        }

        var values = result.Model.Values
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new PluginFeatureValue(
                value.Key,
                FormatFeatureLabel(value.Key),
                value.Value))
            .ToImmutableArray();
        var lines = values.Select(value => $"{value.Label}: {value.Value}")
            .Append($"Provider: {result.ProviderId}");
        ImmutableArray<PluginMutationAction> mutations = [];
        ImmutableArray<PluginReadAction> readActions = [];
        if (_pluginFeatures is not null)
        {
            var availableReads = await _pluginFeatures.GetActionsAsync(
                featureId, SelectedHost.Id, cancellationToken);
            readActions = [.. availableReads.Select(item => new PluginReadAction(
                item.ActionId, item.Title, item.Purpose,
                [.. item.Parameters.Select(parameter => new PluginMutationInput(
                    parameter.Name, parameter.Label, parameter.IsRequired))]))];
        }
        if (_pluginMutations is not null)
        {
            var available = await _pluginMutations.GetAvailableAsync(
                featureId,
                SelectedHost.Id,
                cancellationToken);
            mutations =
            [
                .. available.Select(item => new PluginMutationAction(
                    item.MutationId,
                    item.Title,
                    item.Purpose,
                    item.ParameterName,
                    item.ParameterLabel,
                    item.Parameters.Select(parameter => new PluginMutationInput(
                        parameter.Name,
                        parameter.Label,
                        parameter.IsRequired)).ToImmutableArray(),
                    item.RequiresElevation,
                    item.DisplaysOutput))
            ];
        }

        return new(
            title,
            string.Join(Environment.NewLine, lines),
            Mutations: mutations,
            ReadActions: readActions,
            Values: values,
            ProviderId: result.ProviderId);
    }

    public async Task<PluginMutationDisplay> ExecuteFeatureReadActionAsync(
        string featureId,
        PluginReadAction action,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (_pluginFeatures is null || SelectedHost is null)
            return new(false, "Select and connect a host before reading logs.");
        var result = await _pluginFeatures.ExecuteActionAsync(
            featureId, action.ActionId, SelectedHost.Id, parameters, cancellationToken);
        if (!result.IsSuccess || result.Model is null)
            return new(false, result.SafeFailureMessage ?? "The read action failed.");
        return new(true, result.Model.Values.GetValueOrDefault("Logs", string.Empty));
    }

    public async Task<PluginMutationDisplay> ExecuteFeatureMutationAsync(
        string featureId,
        PluginMutationAction action,
        string value,
        CancellationToken cancellationToken = default)
        => await ExecuteFeatureMutationAsync(
            featureId,
            action,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [action.ParameterName] = value
            },
            cancellationToken);

    public async Task<PluginMutationDisplay> ExecuteFeatureMutationAsync(
        string featureId,
        PluginMutationAction action,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(action);
        if (_pluginMutations is null || SelectedHost is null)
        {
            return new(false, "Select and connect a host before applying a change.");
        }

        var hostId = SelectedHost.Id;
        var result = await _pluginMutations.ExecuteAsync(
            featureId,
            action.MutationId,
            hostId,
            parameters,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return new(
                false,
                result.SafeFailureMessage ?? "The change could not be applied.");
        }

        if (action.DisplaysOutput)
        {
            const int maximumDisplayedOutput = 64 * 1024;
            var output = result.Operation?.Commands.LastOrDefault()?.StandardOutput.Content;
            if (string.IsNullOrWhiteSpace(output))
            {
                output = "The command completed without output.";
            }
            else if (output.Length > maximumDisplayedOutput)
            {
                output = string.Concat(
                    output.AsSpan(0, maximumDisplayedOutput),
                    Environment.NewLine,
                    "[Displayed output truncated]");
            }

            return new(true, output);
        }

        await RefreshSelectedHostDiscoveryAsync(cancellationToken);
        return new(true, "The change was applied and host state was refreshed.");
    }

    public async Task<bool> NeedsSelectedHostPrivilegeCredentialAsync(
        PluginMutationAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!action.RequiresElevation ||
            SelectedHost is null ||
            _privilegeCredentials is null)
        {
            return false;
        }

        var discovery = _discoverySnapshots is null
            ? null
            : await _discoverySnapshots.GetLatestAsync(
                SelectedHost.Id,
                cancellationToken);
        if (discovery?.Capabilities.Contains("privilege.root") == true)
        {
            return false;
        }

        using var credential = _privilegeCredentials.Retrieve(SelectedHost.Id);
        return credential is null;
    }

    public async Task<IReadOnlyList<ActivityItemViewModel>> LoadActivityAsync(
        bool includeDiagnostics,
        CancellationToken cancellationToken = default) =>
        await LoadActivityAsync(
            new ActivityFilterViewModel(IncludeDiagnostics: includeDiagnostics),
            cancellationToken);

    public async Task<IReadOnlyList<ActivityItemViewModel>> LoadActivityAsync(
        ActivityFilterViewModel filter,
        CancellationToken cancellationToken = default) =>
        (await LoadActivityPageAsync(
            filter,
            new ActivityPageCursor(),
            cancellationToken: cancellationToken)).Items;

    public async Task<ActivityPageViewModel> LoadActivityPageAsync(
        ActivityFilterViewModel filter,
        ActivityPageCursor cursor,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(cursor);
        if (pageSize is < 1 or > 100 ||
            cursor.OperationOffset < 0 ||
            cursor.CommandOffset < 0 ||
            cursor.HostTrustOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (_activityReader is null &&
            _operationAuditReader is null &&
            _hostTrustAuditReader is null)
        {
            return new([], null, false);
        }

        var fetchLimit = pageSize + 1;
        var hostProfileId = NullIfWhiteSpace(filter.HostProfileId);
        var pluginId = NullIfWhiteSpace(filter.PluginId);
        var searchText = NullIfWhiteSpace(filter.SearchText);
        var snapshotBefore = cursor.SnapshotBefore;
        var startedBefore = snapshotBefore is not null &&
            (filter.StartedBefore is null ||
             snapshotBefore < filter.StartedBefore)
                ? snapshotBefore
                : filter.StartedBefore;
        OperationAuditStatus? operationStatus = null;
        var hasOperationStatus = true;
        if (filter.Status is not null)
        {
            hasOperationStatus = Enum.TryParse<OperationAuditStatus>(
                filter.Status,
                ignoreCase: true,
                out var parsedOperationStatus);
            operationStatus = hasOperationStatus ? parsedOperationStatus : null;
        }

        var operations = _operationAuditReader is null || !hasOperationStatus
            ? []
            : await _operationAuditReader.QueryOperationsAsync(
                new(
                    hostProfileId,
                    pluginId,
                    filter.Source,
                    operationStatus,
                    filter.Privilege,
                    filter.Risk,
                    filter.StartedAtOrAfter,
                    startedBefore,
                    Offset: cursor.OperationOffset,
                    Limit: fetchLimit,
                    SearchText: searchText),
                cancellationToken);
        AuditStatus? commandStatus = null;
        var hasCommandStatus = true;
        if (filter.Status is not null)
        {
            hasCommandStatus = Enum.TryParse<AuditStatus>(
                filter.Status,
                ignoreCase: true,
                out var parsedCommandStatus);
            commandStatus = hasCommandStatus ? parsedCommandStatus : null;
        }

        var activity = hasCommandStatus && _activityReader is not null
            ? await _activityReader.QueryAsync(
                new(
                    hostProfileId,
                    pluginId,
                    filter.Source,
                    commandStatus,
                    filter.Privilege,
                    filter.Risk,
                    filter.StartedAtOrAfter,
                    startedBefore,
                    filter.IncludeDiagnostics,
                    Offset: cursor.CommandOffset,
                    Limit: fetchLimit,
                    SearchText: searchText,
                    ExcludeOperationCommands: !filter.IncludeDiagnostics),
                cancellationToken)
            : [];
        var includeHostTrustEvents =
            _hostTrustAuditReader is not null &&
            filter.Source is null &&
            (pluginId is null ||
             string.Equals(pluginId, "orvian.core.security", StringComparison.Ordinal)) &&
            (filter.Status is null ||
             string.Equals(filter.Status, "Succeeded", StringComparison.OrdinalIgnoreCase));
        var hostTrustEvents = includeHostTrustEvents
            ? await _hostTrustAuditReader!.QueryHostTrustEventsAsync(
                new(
                    hostProfileId,
                    OccurredAtOrAfter: filter.StartedAtOrAfter,
                    OccurredBefore: startedBefore,
                    Offset: cursor.HostTrustOffset,
                    Limit: fetchLimit,
                    SearchText: searchText),
                cancellationToken)
            : [];
        var candidates = operations.Select(item => new ActivityPageCandidate(
                ActivityPageStream.Operation,
                new ActivityItemViewModel(
                    CommandId: null,
                    item.OperationId,
                    item.StartedAt.ToLocalTime(),
                    item.HostProfileId,
                    item.RemoteUserName,
                    item.PluginId,
                    item.Title,
                    item.Purpose,
                    item.InvocationSource.ToString(),
                    item.Status.ToString(),
                    item.SafeFailureMessage,
                    $"Operation · {item.CommandCount} command attempt" +
                    (item.CommandCount == 1 ? string.Empty : "s"))))
                .Concat(activity
                    .Select(item => new ActivityPageCandidate(
                        ActivityPageStream.Command,
                        new ActivityItemViewModel(
                            item.CommandId,
                            item.OperationId,
                            item.StartedAt.ToLocalTime(),
                            item.HostProfileId,
                            item.RemoteUserName,
                            item.PluginId,
                            item.Executable,
                            string.Join(' ', item.RedactedArguments),
                            item.InvocationSource.ToString(),
                            item.Status.ToString(),
                            item.SafeFailureMessage,
                            "Command attempt"))))
                .Concat(hostTrustEvents.Select(item => new ActivityPageCandidate(
                    ActivityPageStream.HostTrust,
                    new ActivityItemViewModel(
                        CommandId: null,
                        OperationId: item.EventId,
                        item.OccurredAt.ToLocalTime(),
                        item.HostProfileId,
                        "local",
                        "orvian.core.security",
                        item.Action == HostTrustAction.TrustFirstSeen
                            ? "Trusted first-seen host identity"
                            : "Replaced trusted host identity",
                        $"{item.HostName}:{item.Port} · {item.Algorithm} · " +
                        item.ObservedFingerprint,
                        "LocalSecurity",
                        "Succeeded",
                        SafeFailureMessage: null,
                        "Host trust event"))))
                .OrderByDescending(candidate => candidate.Item.StartedAt)
                .ThenBy(candidate => candidate.Stream)
                .ThenBy(candidate =>
                    candidate.Item.CommandId ?? candidate.Item.OperationId)
                .Take(pageSize)
                .ToArray();
        var nextCursor = new ActivityPageCursor(
            cursor.OperationOffset +
                candidates.Count(candidate =>
                    candidate.Stream == ActivityPageStream.Operation),
            cursor.CommandOffset +
                candidates.Count(candidate =>
                    candidate.Stream == ActivityPageStream.Command),
            cursor.HostTrustOffset +
                candidates.Count(candidate =>
                    candidate.Stream == ActivityPageStream.HostTrust),
            snapshotBefore);
        var hasMore =
            operations.Count + activity.Count + hostTrustEvents.Count > candidates.Length;
        return new(
            candidates.Select(candidate => candidate.Item).ToArray(),
            hasMore ? nextCursor : null,
            hasMore);
    }

    private enum ActivityPageStream
    {
        Operation,
        Command,
        HostTrust
    }

    private sealed record ActivityPageCandidate(
        ActivityPageStream Stream,
        ActivityItemViewModel Item);

    public async Task<CommandAuditDetailViewModel?> LoadCommandAuditDetailAsync(
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        if (_commandAuditDetails is null)
        {
            return null;
        }

        var entry = await _commandAuditDetails
            .GetCommandAsync(commandId, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var duration = entry.CompletedAt is null
            ? "Pending"
            : (entry.CompletedAt.Value - entry.StartedAt)
                .ToString("c", System.Globalization.CultureInfo.CurrentCulture);
        return new(
            entry.CommandId,
            entry.OperationId,
            entry.HostProfileId,
            entry.ConnectionId,
            entry.RemoteUserName,
            entry.PluginId,
            entry.PluginVersion,
            entry.Executable,
            string.Join(' ', entry.RedactedArguments),
            entry.Privilege.ToString(),
            entry.InvocationSource.ToString(),
            entry.Status.ToString(),
            entry.StartedAt.ToLocalTime(),
            entry.CompletedAt?.ToLocalTime(),
            duration,
            entry.ExitCode,
            entry.StandardOutputBytes,
            entry.StandardErrorBytes,
            entry.OutputTruncated,
            entry.FailureClassification.ToString(),
            entry.OutputLogging.ToString(),
            entry.RetainedStandardOutput,
            entry.RetainedStandardError,
            entry.SafeFailureMessage);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void NotifyConnectionActionChanged()
    {
        OnPropertyChanged(nameof(IsConnectionAttemptRunning));
        OnPropertyChanged(nameof(ConnectionActionTitle));
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(CanManageSelectedHost));
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt completed between observing and cancelling it.
        }
    }

    private static string? BuildOverview(
        Orvian.Discovery.DiscoverySnapshot? discovery,
        TrustedHostKey? trustedKey)
    {
        if (discovery is null && trustedKey is null)
        {
            return null;
        }

        var lines = new List<string>();
        if (trustedKey is not null)
        {
            lines.Add(
                $"Trusted identity: {trustedKey.Algorithm} · {trustedKey.Sha256Fingerprint}");
        }

        if (discovery is null)
        {
            return string.Join(Environment.NewLine, lines);
        }

        AddFact(lines, discovery, "OS", "os.pretty_name", discovery.OperatingSystem.ToString());
        AddFact(lines, discovery, "Remote hostname", "host.name");
        AddFact(lines, discovery, "Kernel", "kernel.release");
        AddFact(lines, discovery, "Architecture", "kernel.arch");
        AddFact(lines, discovery, "Shell", "user.shell");
        AddFact(lines, discovery, "Boot time", "system.boot_time");
        AddFact(lines, discovery, "Effective UID", "user.effective_uid");
        AddFact(lines, discovery, "Package provider", "package.provider");
        var privilege = discovery.Capabilities.Contains("privilege.root")
            ? "already root"
            : discovery.Capabilities.Contains("privilege.sudo")
                ? "sudo"
                : discovery.Capabilities.Contains("privilege.doas")
                    ? "doas"
                    : "none detected";
        lines.Add($"Privilege provider: {privilege}");
        lines.Add(
            $"Capabilities: {discovery.Capabilities.Count} · " +
            (discovery.IsPartial ? "discovery partial" : "discovery complete"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddFact(
        ICollection<string> lines,
        Orvian.Discovery.DiscoverySnapshot discovery,
        string label,
        string key,
        string? fallback = null)
    {
        var value = discovery.Facts.TryGetValue(key, out var fact)
            ? fact.Value
            : fallback;
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value}");
        }
    }

    private static string FormatFeatureLabel(string key)
    {
        if (key.Length == 0)
        {
            return key;
        }

        var characters = new List<char>(key.Length + 8) { key[0] };
        for (var index = 1; index < key.Length; index++)
        {
            var current = key[index];
            if (char.IsUpper(current) && char.IsLower(key[index - 1]))
            {
                characters.Add(' ');
            }

            characters.Add(current);
        }

        return new string([.. characters]);
    }
}

public sealed record PluginFeatureDisplay(
    string Title,
    string Content,
    bool IsError = false,
    ImmutableArray<PluginMutationAction> Mutations = default,
    ImmutableArray<PluginReadAction> ReadActions = default,
    ImmutableArray<PluginFeatureValue> Values = default,
    string? ProviderId = null);

public sealed record PluginFeatureValue(string Key, string Label, string Value);

public sealed record PluginMutationAction(
    string MutationId,
    string Title,
    string Purpose,
    string ParameterName,
    string ParameterLabel,
    ImmutableArray<PluginMutationInput> Parameters,
    bool RequiresElevation,
    bool DisplaysOutput = false);

public sealed record PluginMutationInput(
    string Name,
    string Label,
    bool IsRequired);

public sealed record PluginMutationDisplay(bool IsSuccess, string Message);

public sealed record PluginReadAction(
    string ActionId,
    string Title,
    string Purpose,
    ImmutableArray<PluginMutationInput> Parameters);

internal sealed record DockerImageFeatureRow(
    string Id,
    string Repository,
    string Tag,
    string Digest,
    string CreatedSince,
    string Size)
{
    public string Reference =>
        Repository is "" or "<none>"
            ? Id
            : Tag is "" or "<none>"
                ? Repository
                : $"{Repository}:{Tag}";
}

internal sealed record DockerContainerFeatureRow(
    string Id,
    string Names,
    string Image,
    string State,
    string Status,
    string Ports,
    string Size,
    string CreatedAt)
{
    public string Identifier => string.IsNullOrWhiteSpace(Names) ? Id : Names;
    public string DisplayName => string.IsNullOrWhiteSpace(Names) ? Id : Names;
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);
}

internal static class DockerFeatureProjection
{
    public static ImmutableArray<DockerImageFeatureRow> ParseImages(
        PluginFeatureDisplay feature) =>
        feature.Values
            .Where(value => value.Key.StartsWith("Image ", StringComparison.Ordinal) &&
                value.Key.EndsWith(".ID", StringComparison.Ordinal))
            .Select(value =>
            {
                var prefix = value.Key[..^3];
                return new DockerImageFeatureRow(
                    value.Value,
                    Value(feature, $"{prefix}.Repository"),
                    Value(feature, $"{prefix}.Tag"),
                    Value(feature, $"{prefix}.Digest"),
                    Value(feature, $"{prefix}.CreatedSince"),
                    Value(feature, $"{prefix}.Size"));
            })
            .OrderBy(image => image.Reference, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    public static ImmutableArray<DockerContainerFeatureRow> ParseContainers(
        PluginFeatureDisplay feature) =>
        feature.Values
            .Where(value => value.Key.StartsWith("Container ", StringComparison.Ordinal) &&
                value.Key.EndsWith(".ID", StringComparison.Ordinal))
            .Select(value =>
            {
                var prefix = value.Key[..^3];
                return new DockerContainerFeatureRow(
                    value.Value,
                    Value(feature, $"{prefix}.Names"),
                    Value(feature, $"{prefix}.Image"),
                    Value(feature, $"{prefix}.State"),
                    Value(feature, $"{prefix}.Status"),
                    Value(feature, $"{prefix}.Ports"),
                    Value(feature, $"{prefix}.Size"),
                    Value(feature, $"{prefix}.CreatedAt"));
            })
            .OrderBy(container => container.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private static string Value(PluginFeatureDisplay feature, string key) =>
        feature.Values.FirstOrDefault(value =>
            string.Equals(value.Key, key, StringComparison.Ordinal))?.Value ?? string.Empty;
}

public sealed record ActivityItemViewModel(
    Guid? CommandId,
    Guid OperationId,
    DateTimeOffset StartedAt,
    string HostProfileId,
    string RemoteUserName,
    string PluginId,
    string Executable,
    string RedactedArguments,
    string Source,
    string Status,
    string? SafeFailureMessage,
    string Kind)
{
    public string Summary =>
        $"{StartedAt:g} · {Status} · {Executable} {RedactedArguments}".TrimEnd();

    public string Details =>
        $"{Kind} · {Source} · {PluginId} · host {HostProfileId} · user {RemoteUserName}" +
        (SafeFailureMessage is null ? string.Empty : $"\n{SafeFailureMessage}");
}

public sealed record ActivityFilterViewModel(
    string? HostProfileId = null,
    string? PluginId = null,
    InvocationSource? Source = null,
    string? Status = null,
    PrivilegeLevel? Privilege = null,
    OperationRisk? Risk = null,
    DateTimeOffset? StartedAtOrAfter = null,
    DateTimeOffset? StartedBefore = null,
    bool IncludeDiagnostics = false,
    string? SearchText = null);

public sealed record ActivityPageCursor(
    int OperationOffset = 0,
    int CommandOffset = 0,
    int HostTrustOffset = 0,
    DateTimeOffset? SnapshotBefore = null);

public sealed record ActivityPageViewModel(
    IReadOnlyList<ActivityItemViewModel> Items,
    ActivityPageCursor? NextCursor,
    bool HasMore);

public sealed record DiagnosticItemViewModel(
    DateTimeOffset OccurredAt,
    string Severity,
    string Category,
    string Code,
    string SafeMessage,
    Guid CorrelationId,
    string Properties)
{
    public string Summary =>
        $"{OccurredAt:g} · {Severity} · {Code} · {SafeMessage}";

    public string Details =>
        $"Category: {Category}\nCorrelation: {CorrelationId:D}" +
        (string.IsNullOrEmpty(Properties) ? string.Empty : $"\n{Properties}");
}

public sealed record CommandAuditDetailViewModel(
    Guid CommandId,
    Guid OperationId,
    string HostProfileId,
    string ConnectionId,
    string RemoteUserName,
    string PluginId,
    string PluginVersion,
    string Executable,
    string RedactedArguments,
    string Privilege,
    string Source,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Duration,
    int? ExitCode,
    long StandardOutputBytes,
    long StandardErrorBytes,
    bool OutputTruncated,
    string FailureClassification,
    string OutputLogging,
    string? RetainedStandardOutput,
    string? RetainedStandardError,
    string? SafeFailureMessage)
{
    public string Content =>
        $"Command ID: {CommandId}\n" +
        $"Operation ID: {OperationId}\n" +
        $"Host: {HostProfileId}\n" +
        $"Connection: {ConnectionId}\n" +
        $"Authenticated user: {RemoteUserName}\n" +
        $"Plugin: {PluginId} {PluginVersion}\n" +
        $"Source: {Source}\n" +
        $"Privilege: {Privilege}\n" +
        $"Status: {Status}\n" +
        $"Started: {StartedAt:g}\n" +
        $"Completed: {(CompletedAt is null ? "Pending" : CompletedAt.Value.ToString("g"))}\n" +
        $"Duration: {Duration}\n" +
        $"Executable: {Executable}\n" +
        $"Arguments: {RedactedArguments}\n" +
        $"Exit code: {(ExitCode?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "None")}\n" +
        $"Output: {StandardOutputBytes} stdout bytes, {StandardErrorBytes} stderr bytes\n" +
        $"Truncated: {OutputTruncated}\n" +
        $"Failure classification: {FailureClassification}\n" +
        $"Output policy: {OutputLogging}\n" +
        $"Retained stdout: {FormatRetained(RetainedStandardOutput)}\n" +
        $"Retained stderr: {FormatRetained(RetainedStandardError)}" +
        (SafeFailureMessage is null ? string.Empty : $"\nFailure: {SafeFailureMessage}");

    private static string FormatRetained(string? value) =>
        value is null ? "Not retained" : value.Length == 0 ? "(empty)" : value;
}
