using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Collections.Immutable;
using System.Globalization;
using Orvian.Application.Settings;
using Orvian.Core.Commands;

namespace Orvian.Shell;

public sealed class MainWindow : Window
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private FileTransferView? _activeFileTransferView;
    private bool _closeConfirmed;

    public MainWindow(
        MainWindowViewModel viewModel,
        InteractiveHostKeyDecisionService? hostKeyDecisions = null,
        InteractiveOperationConfirmationService? operationConfirmations = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        Title = viewModel.ProductName;
        Width = 1180;
        Height = 780;
        MinWidth = 860;
        MinHeight = 600;
        DataContext = viewModel;
        ApplyPresentationSettings(viewModel.ApplicationSettings);
        viewModel.SettingsChanged += ApplyPresentationSettings;
        if (hostKeyDecisions is not null)
        {
            hostKeyDecisions.DecisionHandler = ShowHostKeyDecisionAsync;
        }
        if (operationConfirmations is not null)
        {
            operationConfirmations.ConfirmationHandler =
                (request, _) => ShowOperationConfirmationAsync(
                    ResolveActiveDialogOwner(),
                    request);
        }

        Content = BuildLayout(
            this,
            viewModel,
            view => _activeFileTransferView = view);
        Closing += async (_, args) =>
        {
            if (_closeConfirmed || _activeFileTransferView is null)
            {
                return;
            }

            args.Cancel = true;
            if (await _activeFileTransferView.ConfirmCleanupAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            viewModel.SettingsChanged -= ApplyPresentationSettings;
            viewModel.CancelConnectionAttempt();
        };
    }

    private static void ApplyPresentationSettings(ApplicationSettings settings)
    {
        if (Avalonia.Application.Current is not null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = settings.Appearance switch
            {
                AppearancePreference.Light => ThemeVariant.Light,
                AppearancePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }

        var culture = settings.CultureName is null
            ? SystemCulture
            : CultureInfo.GetCultureInfo(settings.CultureName);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static Control BuildLayout(
        Window owner,
        MainWindowViewModel viewModel,
        Action<FileTransferView?> setActiveFileTransferView)
    {
        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            RowDefinitions = new RowDefinitions("*")
        };

        var hostOverview = BuildContent(owner, viewModel);
        var content = new ContentControl { Content = hostOverview };
        Grid.SetColumn(content, 1);

        async Task NavigateAsync(Orvian.UI.Abstractions.ShellNavigationItem item)
        {
            if (content.Content is FileTransferView currentFiles)
            {
                if (string.Equals(item.FeatureId, "file-transfer", StringComparison.Ordinal))
                {
                    return;
                }

                if (!await currentFiles.ConfirmCleanupAsync())
                {
                    var filesItem = viewModel.NavigationItems.FirstOrDefault(candidate =>
                        string.Equals(candidate.FeatureId, "file-transfer", StringComparison.Ordinal));
                    if (filesItem is not null)
                    {
                        viewModel.SelectedNavigationItem = filesItem;
                    }
                    return;
                }

                setActiveFileTransferView(null);
            }

            if (item.FeatureId is null)
            {
                content.Content = hostOverview;
                return;
            }

            if (string.Equals(item.FeatureId, "file-transfer", StringComparison.Ordinal))
            {
                var fileTransferView = new FileTransferView(owner, viewModel);
                setActiveFileTransferView(fileTransferView);
                content.Content = fileTransferView;
                return;
            }

            var feature = await viewModel.LoadFeatureAsync(item.FeatureId, item.Title);
            if (!string.Equals(
                    viewModel.SelectedNavigationItem.Id,
                    item.Id,
                    StringComparison.Ordinal))
            {
                return;
            }

            content.Content = new FeatureResultView(
                owner,
                feature,
                item.FeatureId,
                viewModel,
                () => viewModel.SelectedNavigationItem = viewModel.NavigationItems[0]);
        }

        root.Children.Add(BuildSidebar(owner, viewModel, NavigateAsync));
        root.Children.Add(content);

        return root;
    }

    private static Control BuildSidebar(
        Window owner,
        MainWindowViewModel viewModel,
        Func<Orvian.UI.Abstractions.ShellNavigationItem, Task> navigateAsync)
    {
        var sidebar = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(18, 24, 18, 18),
            RowSpacing = 12
        };

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 11,
            Margin = new Thickness(4, 0, 4, 8)
        };
        var brandMark = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#5B6CFF")),
            Child = new TextBlock
            {
                Text = "O",
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var title = new TextBlock
        {
            FontSize = 21,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(MainWindowViewModel.ProductName)));
        brand.Children.Add(brandMark);
        brand.Children.Add(title);
        sidebar.Children.Add(brand);

        var search = new TextBox
        {
            Height = 38,
            CornerRadius = new CornerRadius(9)
        };
        AutomationProperties.SetName(search, "Search hosts");
        search.Bind(
            TextBox.PlaceholderTextProperty,
            new Binding(nameof(MainWindowViewModel.HostSearchWatermark)));
        search.Bind(
            TextBox.TextProperty,
            new Binding(nameof(MainWindowViewModel.SearchText))
            {
                Mode = BindingMode.TwoWay
            });
        CancellationTokenSource? searchCancellation = null;
        search.TextChanged += async (_, _) =>
        {
            searchCancellation?.Cancel();
            searchCancellation?.Dispose();
            searchCancellation = new CancellationTokenSource();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), searchCancellation.Token);
                await viewModel.LoadHostsAsync(searchCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        };
        owner.Closed += (_, _) =>
        {
            searchCancellation?.Cancel();
            searchCancellation?.Dispose();
            searchCancellation = null;
        };
        Grid.SetRow(search, 1);
        sidebar.Children.Add(search);

        var featuresLabel = CreateSectionLabel("FEATURES");
        Grid.SetRow(featuresLabel, 2);
        sidebar.Children.Add(featuresLabel);

        var navigation = new ListBox
        {
            MaxHeight = 230,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10)
        };
        navigation.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainWindowViewModel.NavigationItems)));
        navigation.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(MainWindowViewModel.SelectedNavigationItem))
            {
                Mode = BindingMode.TwoWay
            });
        navigation.ItemTemplate = new FuncDataTemplate<Orvian.UI.Abstractions.ShellNavigationItem>(
            (item, _) => new TextBlock
            {
                Text = item?.Title,
                Padding = new Thickness(8)
            });
        navigation.SelectionChanged += async (_, _) =>
        {
            var item = viewModel.SelectedNavigationItem;
            await navigateAsync(item);
        };
        Grid.SetRow(navigation, 3);
        sidebar.Children.Add(navigation);

        var hostRegion = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var hostHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        hostHeader.Children.Add(CreateSectionLabel("HOSTS"));
        var addHost = new Button
        {
            Content = "+  Add",
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(addHost, "Add Host");
        addHost.Click += async (_, _) => await ShowAddHostAsync(owner, viewModel);
        Grid.SetColumn(addHost, 1);
        hostHeader.Children.Add(addHost);
        hostRegion.Children.Add(hostHeader);

        var hosts = new ListBox
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10)
        };
        hosts.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainWindowViewModel.Hosts)));
        hosts.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(MainWindowViewModel.SelectedHost))
            {
                Mode = BindingMode.TwoWay
            });
        hosts.ItemTemplate = new FuncDataTemplate<HostListItemViewModel>(
            (item, _) =>
            {
                var panel = new StackPanel
                {
                    Margin = new Thickness(10, 8),
                    Spacing = 4
                };
                panel.Children.Add(new TextBlock
                {
                    Text = item?.DisplayName,
                    FontWeight = FontWeight.SemiBold
                });
                panel.Children.Add(new TextBlock
                {
                    Text = item is null ? string.Empty : $"{item.Endpoint} · {item.StatusText}",
                    FontSize = 12
                });
                panel.Children.Add(new TextBlock
                {
                    Text = item?.CachedDiscoveryText,
                    FontSize = 11,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                });
                return panel;
            });
        Grid.SetRow(hosts, 1);
        hostRegion.Children.Add(hosts);
        Grid.SetRow(hostRegion, 4);
        sidebar.Children.Add(hostRegion);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var activity = new Button();
        activity.Bind(ContentControl.ContentProperty, new Binding(nameof(MainWindowViewModel.ActivityTitle)));
        activity.Click += async (_, _) =>
            await new ActivityWindow(viewModel).ShowDialog(owner);
        var settings = new Button();
        settings.Bind(ContentControl.ContentProperty, new Binding(nameof(MainWindowViewModel.SettingsTitle)));
        settings.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanManageSettings)));
        settings.Click += async (_, _) =>
            await new SettingsWindow(viewModel).ShowDialog(owner);
        footer.Children.Add(activity);
        footer.Children.Add(settings);
        Grid.SetRow(footer, 5);
        sidebar.Children.Add(footer);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(22, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebar
        };
    }

    private static TextBlock CreateSectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Opacity = 0.58,
        Margin = new Thickness(8, 4)
    };

    private static async Task ShowAddHostAsync(
        Window owner,
        MainWindowViewModel viewModel)
    {
        var input = await new AddHostWindow(
            defaults: viewModel.DefaultConnectionPreferences,
            persistentCredentialStorageAvailable:
                viewModel.CanRememberCredentials)
            .ShowDialog<AddHostInput?>(owner);
        if (input is null)
        {
            return;
        }

        try
        {
            await viewModel.AddHostAsync(
                input.DisplayName,
                input.HostName,
                input.Port,
                input.UserName,
                input.Password.AsMemory(),
                input.RememberCredential,
                input.AuthenticationMethod,
                input.PrivateKeyPath,
                input.Tags,
                input.Notes,
                input.ConnectionPreferences);
        }
        catch (Exception)
        {
            await MessageWindow.ShowAsync(
                owner,
                "Host not saved",
                "Check the host details and secure-store availability, then try again.");
        }
    }

    private static Task<bool> ShowOperationConfirmationAsync(
        Window owner,
        Orvian.Core.Commands.OperationConfirmationRequest request) =>
        new ConfirmationWindow(
            request.Intent.Title,
            $"{request.Intent.Purpose}\n\n" +
            $"Target host: {request.Intent.HostProfileId}\n" +
            $"Risk: {request.Intent.Risk}\n" +
            $"Privilege: {request.Intent.Privilege}\n\n" +
            $"Technical details:\n{string.Join(Environment.NewLine, request.RedactedCommands)}",
            "Continue").ShowDialog<bool>(owner);

    private Window ResolveActiveDialogOwner()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            desktop)
        {
            return desktop.Windows.LastOrDefault(window =>
                       window.IsVisible && window.IsActive) ?? this;
        }

        return this;
    }

    private static Control BuildContent(Window owner, MainWindowViewModel viewModel)
    {
        var panel = new StackPanel
        {
            MaxWidth = 780,
            Margin = new Thickness(42, 40, 42, 48),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Spacing = 18
        };

        var title = new TextBlock
        {
            FontSize = 32,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(MainWindowViewModel.ContentTitle)));

        var subtitle = new TextBlock
        {
            FontSize = 14,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, -10, 0, 0)
        };
        subtitle.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(MainWindowViewModel.ContentSubtitle)));

        var description = new TextBlock
        {
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 21,
            Opacity = 0.82
        };
        description.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(MainWindowViewModel.ContentDetails)));

        var addHost = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetName(addHost, "Add Host");
        addHost.Bind(ContentControl.ContentProperty, new Binding(nameof(MainWindowViewModel.PrimaryActionTitle)));
        addHost.Bind(
            Visual.IsVisibleProperty,
            new Binding(nameof(MainWindowViewModel.ShowPrimaryAddHost)));
        addHost.Click += async (_, _) => await ShowAddHostAsync(owner, viewModel);

        panel.Children.Add(title);
        panel.Children.Add(subtitle);
        panel.Children.Add(CreateSectionLabel("SYSTEM OVERVIEW"));
        panel.Children.Add(new Border
        {
            Padding = new Thickness(22),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(42, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = description
        });
        var primaryActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 10,
            LineSpacing = 10
        };
        var managementActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 10,
            LineSpacing = 10
        };
        var connect = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 9),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.Parse("#5B6CFF")),
            Foreground = Brushes.White
        };
        AutomationProperties.SetName(connect, "Connect");
        connect.Bind(
            ContentControl.ContentProperty,
            new Binding(nameof(MainWindowViewModel.ConnectionActionTitle)));
        connect.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanToggleConnection)));
        connect.Click += async (_, _) =>
        {
            try
            {
                var state = await viewModel.ToggleSelectedHostConnectionAsync();
                if (state == Orvian.Connections.ConnectionState.AuthenticationFailed)
                {
                    var credential = await new SessionCredentialWindow()
                        .ShowDialog<char[]?>(owner);
                    if (credential is not null)
                    {
                        try
                        {
                            viewModel.SetSelectedHostSessionCredential(credential);
                            await viewModel.ToggleSelectedHostConnectionAsync();
                        }
                        finally
                        {
                            Array.Clear(credential);
                        }
                    }
                }
            }
            catch (Exception)
            {
                await MessageWindow.ShowAsync(
                    owner,
                    "Connection failed",
                    "The host connection could not be completed. Review the host and credential settings.");
            }
        };
        primaryActions.Children.Add(connect);
        var refreshDiscovery = new Button
        {
            Content = "Refresh Discovery",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 9),
            CornerRadius = new CornerRadius(9)
        };
        refreshDiscovery.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanRefreshDiscovery)));
        refreshDiscovery.Click += async (_, _) =>
        {
            try
            {
                await viewModel.RefreshSelectedHostDiscoveryAsync();
            }
            catch (Exception)
            {
                await MessageWindow.ShowAsync(
                    owner,
                    "Discovery refresh failed",
                    "The host must remain connected while discovery is refreshed.");
            }
        };
        primaryActions.Children.Add(refreshDiscovery);
        var privilegeCredential = new Button
        {
            Content = "Set Privilege Credential",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 9),
            CornerRadius = new CornerRadius(9)
        };
        privilegeCredential.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanSetPrivilegeCredential)));
        privilegeCredential.Click += async (_, _) =>
        {
            var credential = await new PrivilegeCredentialWindow()
                .ShowDialog<char[]?>(owner);
            if (credential is null)
            {
                return;
            }

            try
            {
                viewModel.SetSelectedHostPrivilegeCredential(credential);
            }
            finally
            {
                Array.Clear(credential);
            }
        };
        primaryActions.Children.Add(privilegeCredential);
        panel.Children.Add(primaryActions);
        panel.Children.Add(CreateSectionLabel("HOST MANAGEMENT"));
        var toggleHostEnabled = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(9)
        };
        toggleHostEnabled.Bind(
            ContentControl.ContentProperty,
            new Binding(nameof(MainWindowViewModel.HostEnabledActionTitle)));
        toggleHostEnabled.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanManageSelectedHost)));
        toggleHostEnabled.Click += async (_, _) =>
        {
            try
            {
                await viewModel.ToggleSelectedHostEnabledAsync();
            }
            catch (Exception)
            {
                await MessageWindow.ShowAsync(
                    owner,
                    "Host state not changed",
                    "The host could not be enabled or disabled safely. Reload it and try again.");
            }
        };
        managementActions.Children.Add(toggleHostEnabled);
        var editHost = new Button
        {
            Content = "Edit Host",
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(9)
        };
        editHost.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanManageSelectedHost)));
        editHost.Click += async (_, _) =>
        {
            try
            {
                var existing = await viewModel.GetSelectedHostProfileAsync();
                if (existing is null)
                {
                    return;
                }

                var input = await new AddHostWindow(new(
                    existing.DisplayName,
                    existing.HostName,
                    existing.Port,
                    existing.UserName,
                    string.Empty,
                    RememberCredential: false,
                    existing.AuthenticationMethod,
                    existing.PrivateKeyPath,
                    existing.Tags,
                    existing.Notes,
                    existing.ConnectionPreferences),
                    hasRememberedCredential:
                        existing.CredentialSecretReference is not null,
                    persistentCredentialStorageAvailable:
                        viewModel.CanRememberCredentials)
                    .ShowDialog<AddHostInput?>(owner);
                if (input is null)
                {
                    return;
                }

                await viewModel.UpdateSelectedHostAsync(
                    existing,
                    input.DisplayName,
                    input.HostName,
                    input.Port,
                    input.UserName,
                    input.Password.AsMemory(),
                    input.RememberCredential,
                    input.AuthenticationMethod,
                    input.PrivateKeyPath,
                    input.Tags,
                    input.Notes,
                    input.ConnectionPreferences,
                    input.RemoveRememberedCredential);
            }
            catch (Exception)
            {
                await MessageWindow.ShowAsync(
                    owner,
                    "Host not updated",
                    "The host changed elsewhere or the new details could not be saved.");
            }
        };
        managementActions.Children.Add(editHost);
        var duplicateHost = new Button
        {
            Content = "Duplicate Host",
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(9)
        };
        duplicateHost.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanManageSelectedHost)));
        duplicateHost.Click += async (_, _) =>
        {
            try
            {
                await viewModel.DuplicateSelectedHostAsync();
            }
            catch (Exception)
            {
                await MessageWindow.ShowAsync(
                    owner,
                    "Host not duplicated",
                    "The host copy could not be saved. Reload the inventory and try again.");
            }
        };
        managementActions.Children.Add(duplicateHost);
        var deleteHost = new Button
        {
            Content = "Delete Host",
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(9),
            Foreground = new SolidColorBrush(Color.Parse("#FF6B6B"))
        };
        deleteHost.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanManageSelectedHost)));
        deleteHost.Click += async (_, _) =>
        {
            if (viewModel.SelectedHost is null)
            {
                return;
            }

            var confirmed = await new ConfirmationWindow(
                "Delete Host?",
                $"Delete “{viewModel.SelectedHost.DisplayName}”? " +
                "Its host profile and cached discovery data will be removed. Audit history is retained.",
                "Delete host").ShowDialog<bool>(owner);
            if (!confirmed)
            {
                return;
            }

            try
            {
                await viewModel.DeleteSelectedHostAsync();
            }
            catch (Exception)
            {
                await MessageWindow.ShowAsync(
                    owner,
                    "Host not deleted",
                    "The host could not be deleted safely. Try again after disconnecting.");
            }
        };
        managementActions.Children.Add(deleteHost);
        panel.Children.Add(new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(12, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = managementActions
        });
        panel.Children.Add(addHost);
        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private async Task<Orvian.Application.Connections.HostKeyUserDecision>
        ShowHostKeyDecisionAsync(
            Orvian.Security.HostKeyVerificationResult verification,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await new HostKeyDecisionWindow(verification)
            .ShowDialog<Orvian.Application.Connections.HostKeyUserDecision>(this);
    }
}

internal sealed record AddHostInput(
    string DisplayName,
    string HostName,
    int Port,
    string UserName,
    string Password,
    bool RememberCredential,
    Orvian.Core.Hosts.HostAuthenticationMethod AuthenticationMethod,
    string? PrivateKeyPath,
    System.Collections.Immutable.ImmutableArray<string> Tags,
    string? Notes,
    Orvian.Core.Hosts.HostConnectionPreferences ConnectionPreferences,
    bool RemoveRememberedCredential = false);

internal sealed class AddHostWindow : Window
{
    private readonly TextBox _displayName = new();
    private readonly TextBox _hostName = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 22 };
    private readonly TextBox _userName = new();
    private readonly TextBox _password = new() { PasswordChar = '●' };
    private readonly CheckBox _privateKey = new() { Content = "Authenticate with a private key" };
    private readonly TextBox _privateKeyPath = new()
    {
        PlaceholderText = "/Users/name/.ssh/id_ed25519",
        IsVisible = false
    };
    private readonly CheckBox _remember = new() { Content = "Remember in the system secure store" };
    private readonly CheckBox _removeRemembered = new()
    {
        Content = "Remove the currently remembered credential",
        IsVisible = false
    };
    private readonly TextBox _tags = new()
    {
        PlaceholderText = "production, database"
    };
    private readonly TextBox _notes = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Top,
        MinHeight = 70
    };
    private readonly NumericUpDown _connectionTimeoutSeconds = new()
    {
        Minimum = 1,
        Maximum = 120,
        Value = 15
    };
    private readonly NumericUpDown _maximumReconnectAttempts = new()
    {
        Minimum = 0,
        Maximum = 5,
        Value = 2
    };

    public AddHostWindow(
        AddHostInput? existing = null,
        Orvian.Core.Hosts.HostConnectionPreferences? defaults = null,
        bool hasRememberedCredential = false,
        bool persistentCredentialStorageAvailable = true)
    {
        Title = existing is null ? "Add Host" : "Edit Host";
        Width = 460;
        Height = 850;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (existing is null && defaults is not null)
        {
            _connectionTimeoutSeconds.Value =
                (decimal)defaults.ConnectionTimeout.TotalSeconds;
            _maximumReconnectAttempts.Value = defaults.MaximumReconnectAttempts;
        }

        var form = new StackPanel { Margin = new Thickness(24), Spacing = 8 };
        if (existing is not null)
        {
            _displayName.Text = existing.DisplayName;
            _hostName.Text = existing.HostName;
            _port.Value = existing.Port;
            _userName.Text = existing.UserName;
            _privateKey.IsChecked =
                existing.AuthenticationMethod ==
                Orvian.Core.Hosts.HostAuthenticationMethod.PrivateKey;
            _privateKeyPath.Text = existing.PrivateKeyPath;
            _tags.Text = string.Join(", ", existing.Tags);
            _notes.Text = existing.Notes;
            _connectionTimeoutSeconds.Value =
                (decimal)existing.ConnectionPreferences.ConnectionTimeout.TotalSeconds;
            _maximumReconnectAttempts.Value =
                existing.ConnectionPreferences.MaximumReconnectAttempts;
        }

        AddField(form, "Display name", _displayName);
        AddField(form, "Host name or IP address", _hostName);
        AddField(form, "Port", _port);
        AddField(form, "User name", _userName);
        form.Children.Add(_privateKey);
        var privateKeyPathLabel = new TextBlock
        {
            Text = "Private key path",
            IsVisible = false
        };
        form.Children.Add(privateKeyPathLabel);
        form.Children.Add(_privateKeyPath);
        var credentialLabel = new TextBlock { Text = "Password" };
        form.Children.Add(credentialLabel);
        form.Children.Add(_password);
        _privateKey.Click += (_, _) =>
        {
            var usesKey = _privateKey.IsChecked == true;
            privateKeyPathLabel.IsVisible = usesKey;
            _privateKeyPath.IsVisible = usesKey;
            credentialLabel.Text = usesKey ? "Key passphrase (optional)" : "Password";
        };
        _remember.IsEnabled = persistentCredentialStorageAvailable;
        form.Children.Add(_remember);
        if (!persistentCredentialStorageAvailable)
        {
            _remember.Content = "Remember credential (secure storage unavailable)";
            form.Children.Add(new TextBlock
            {
                Text =
                    "Credentials entered here will be kept only for this application session. " +
                    "Orvian never falls back to plaintext storage.",
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (existing is not null && hasRememberedCredential)
        {
            form.Children.Add(new TextBlock
            {
                Text =
                    "A credential is currently stored securely. Its value is never displayed. " +
                    "Leave both options unchecked to keep it.",
                TextWrapping = TextWrapping.Wrap
            });
            _remember.Content = persistentCredentialStorageAvailable
                ? "Replace the remembered credential"
                : "Replace remembered credential (secure storage unavailable)";
            _removeRemembered.IsVisible = true;
            form.Children.Add(_removeRemembered);
            _remember.Click += (_, _) =>
            {
                if (_remember.IsChecked == true)
                {
                    _removeRemembered.IsChecked = false;
                }
            };
            _removeRemembered.Click += (_, _) =>
            {
                if (_removeRemembered.IsChecked == true)
                {
                    _remember.IsChecked = false;
                }
            };
        }
        AddField(form, "Tags (comma separated)", _tags);
        AddField(form, "Notes", _notes);
        AddField(form, "Connection timeout (seconds)", _connectionTimeoutSeconds);
        AddField(form, "Additional network retry attempts", _maximumReconnectAttempts);

        var validation = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap
        };
        form.Children.Add(validation);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close(null);
        var save = new Button { Content = "Save host" };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_displayName.Text) ||
                string.IsNullOrWhiteSpace(_hostName.Text) ||
                string.IsNullOrWhiteSpace(_userName.Text) ||
                _port.Value is null ||
                _connectionTimeoutSeconds.Value is null ||
                _maximumReconnectAttempts.Value is null ||
                (_privateKey.IsChecked == true &&
                 string.IsNullOrWhiteSpace(_privateKeyPath.Text)))
            {
                validation.Text = "Display name, host, port, and user name are required.";
                return;
            }

            var tags = (_tags.Text ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries |
                            StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            Close(new AddHostInput(
                _displayName.Text.Trim(),
                _hostName.Text.Trim(),
                decimal.ToInt32(_port.Value.Value),
                _userName.Text.Trim(),
                _password.Text ?? string.Empty,
                _remember.IsChecked == true,
                _privateKey.IsChecked == true
                    ? Orvian.Core.Hosts.HostAuthenticationMethod.PrivateKey
                    : Orvian.Core.Hosts.HostAuthenticationMethod.Password,
                _privateKey.IsChecked == true ? _privateKeyPath.Text?.Trim() : null,
                tags,
                string.IsNullOrWhiteSpace(_notes.Text) ? string.Empty : _notes.Text.Trim(),
                new Orvian.Core.Hosts.HostConnectionPreferences(
                    TimeSpan.FromSeconds(
                        decimal.ToDouble(_connectionTimeoutSeconds.Value.Value)),
                    decimal.ToInt32(_maximumReconnectAttempts.Value.Value)),
                _removeRemembered.IsChecked == true));
        };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        form.Children.Add(actions);
        Content = form;
        if (_privateKey.IsChecked == true)
        {
            privateKeyPathLabel.IsVisible = true;
            _privateKeyPath.IsVisible = true;
            credentialLabel.Text = "Key passphrase (optional)";
        }
    }

    private static void AddField(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(input);
    }
}

internal sealed class MessageWindow : Window
{
    private MessageWindow(string title, string message)
    {
        Title = title;
        Width = 420;
        Height = 190;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var close = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                close
            }
        };
    }

    public static Task ShowAsync(Window owner, string title, string message) =>
        new MessageWindow(title, message).ShowDialog(owner);
}

internal sealed class SessionCredentialWindow : Window
{
    public SessionCredentialWindow()
    {
        Title = "SSH Credential";
        Width = 430;
        Height = 230;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var password = new TextBox { PasswordChar = '●' };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close(null);
        var connect = new Button { Content = "Connect for this session" };
        connect.Click += (_, _) =>
        {
            var credential = (password.Text ?? string.Empty).ToCharArray();
            password.Text = string.Empty;
            Close(credential);
        };
        actions.Children.Add(cancel);
        actions.Children.Add(connect);
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Enter the SSH password. It will be kept only for this application session.",
                    TextWrapping = TextWrapping.Wrap
                },
                password,
                actions
            }
        };
    }
}

internal sealed class PrivilegeCredentialWindow : Window
{
    public PrivilegeCredentialWindow()
    {
        Title = "Privilege Credential";
        Width = 460;
        Height = 240;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var password = new TextBox { PasswordChar = '●' };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close(null);
        var use = new Button { Content = "Use for this connection" };
        use.Click += (_, _) =>
        {
            var credential = (password.Text ?? string.Empty).ToCharArray();
            password.Text = string.Empty;
            Close(credential);
        };
        actions.Children.Add(cancel);
        actions.Children.Add(use);
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text =
                        "Enter the sudo credential. Orvian keeps it in memory only " +
                        "for the selected connection and never exposes it to plugins.",
                    TextWrapping = TextWrapping.Wrap
                },
                password,
                actions
            }
        };
    }
}

internal sealed class ConfirmationWindow : Window
{
    public ConfirmationWindow(
        string title,
        string message,
        string confirmText)
    {
        Title = title;
        Width = 470;
        Height = 230;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close(false);
        var confirm = new Button { Content = confirmText };
        confirm.Click += (_, _) => Close(true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                actions
            }
        };
    }
}

internal sealed class HostKeyDecisionWindow : Window
{
    public HostKeyDecisionWindow(Orvian.Security.HostKeyVerificationResult verification)
    {
        Title = verification.Decision == Orvian.Security.HostKeyDecision.Unknown
            ? "Trust Host Identity?"
            : "Host Identity Changed";
        Width = 560;
        Height = 410;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var observed = verification.Observed;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = verification.Decision == Orvian.Security.HostKeyDecision.Unknown
                ? "This host has not been trusted before. Verify the fingerprint through a separate trusted channel."
                : "The observed host identity does not match the trusted record. This can indicate a rebuilt host or an attack.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text =
                $"Host: {observed.HostName}:{observed.Port}\n" +
                $"Resolved address: {observed.ResolvedAddress ?? "Unavailable"}\n" +
                $"Algorithm: {observed.Algorithm}\n" +
                $"Observed fingerprint: {observed.Sha256Fingerprint}" +
                (verification.Previous is null
                    ? string.Empty
                    : $"\nPreviously trusted fingerprint: " +
                      $"{verification.Previous.Sha256Fingerprint}\n" +
                      $"Previously trusted address: " +
                      $"{verification.Previous.ResolvedAddress ?? "Not recorded"}"),
            FontFamily = FontFamily.Default,
            TextWrapping = TextWrapping.Wrap
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var reject = new Button { Content = "Reject" };
        reject.Click += (_, _) => Close(
            Orvian.Application.Connections.HostKeyUserDecision.Reject);
        var trust = new Button
        {
            Content = verification.Decision == Orvian.Security.HostKeyDecision.Unknown
                ? "Trust and connect"
                : "Replace trusted key"
        };
        trust.Click += (_, _) => Close(
            verification.Decision == Orvian.Security.HostKeyDecision.Unknown
                ? Orvian.Application.Connections.HostKeyUserDecision.TrustFirstSeen
                : Orvian.Application.Connections.HostKeyUserDecision.ReplaceChanged);
        actions.Children.Add(reject);
        actions.Children.Add(trust);
        panel.Children.Add(actions);
        Content = panel;
    }
}

internal sealed class ActivityWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ListBox _items = new();
    private readonly List<ActivityItemViewModel> _loadedItems = [];
    private ActivityFilterViewModel? _activeFilter;
    private ActivityPageCursor _cursor = new();
    private CancellationTokenSource? _activityLoadCancellation;
    private readonly TextBox _host = new()
    {
        Width = 150,
        PlaceholderText = "Host profile ID"
    };
    private readonly TextBox _plugin = new()
    {
        Width = 150,
        PlaceholderText = "Plugin ID"
    };
    private readonly TextBox _search = new()
    {
        Width = 220,
        MaxLength = 200,
        PlaceholderText = "Search safe activity metadata"
    };
    private readonly ComboBox _source = new()
    {
        Width = 145,
        ItemsSource = new[] { "All sources" }
            .Concat(Enum.GetNames<InvocationSource>())
            .ToArray(),
        SelectedIndex = 0
    };
    private readonly ComboBox _status = new()
    {
        Width = 180,
        ItemsSource = new[]
        {
            "All statuses",
            "Started",
            "Succeeded",
            "PartiallySucceeded",
            "Failed",
            "Denied",
            "ValidationDenied",
            "PermissionDenied",
            "AuditUnavailable",
            "Cancelled",
            "TimedOut",
            "Interrupted",
            "CompletionPersistenceFailed"
        },
        SelectedIndex = 0
    };
    private readonly ComboBox _privilege = new()
    {
        Width = 145,
        ItemsSource = new[] { "All privilege" }
            .Concat(Enum.GetNames<PrivilegeLevel>())
            .ToArray(),
        SelectedIndex = 0
    };
    private readonly ComboBox _risk = new()
    {
        Width = 130,
        ItemsSource = new[] { "All risk" }
            .Concat(Enum.GetNames<OperationRisk>())
            .ToArray(),
        SelectedIndex = 0
    };
    private readonly TextBox _from = new()
    {
        Width = 110,
        PlaceholderText = "From YYYY-MM-DD"
    };
    private readonly TextBox _to = new()
    {
        Width = 110,
        PlaceholderText = "To YYYY-MM-DD"
    };
    private readonly TextBlock _filterError = new()
    {
        Foreground = Brushes.IndianRed,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly CheckBox _diagnostics = new()
    {
        Content = "Include discovery diagnostics"
    };
    private readonly Button _details = new()
    {
        Content = "View command details",
        IsEnabled = false
    };
    private readonly Button _loadMore = new()
    {
        Content = "Load more",
        IsEnabled = false
    };

    public ActivityWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Activity";
        Width = 820;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _items.ItemTemplate = new FuncDataTemplate<ActivityItemViewModel>(
            (item, _) => new StackPanel
            {
                Margin = new Thickness(8),
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = item?.Summary,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = item?.Details,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            });
        _items.SelectionChanged += (_, _) =>
        {
            _details.IsEnabled =
                (_items.SelectedItem as ActivityItemViewModel)?.CommandId is not null;
        };
        _details.Click += async (_, _) =>
        {
            if ((_items.SelectedItem as ActivityItemViewModel)?.CommandId is not
                { } commandId)
            {
                return;
            }

            var detail = await _viewModel.LoadCommandAuditDetailAsync(commandId);
            if (detail is not null)
            {
                await new CommandAuditDetailWindow(detail).ShowDialog(this);
            }
        };
        _loadMore.Click += async (_, _) => await LoadNextPageAsync();
        var apply = new Button { Content = "Apply filters" };
        apply.Click += async (_, _) => await RefreshAsync();
        var clear = new Button { Content = "Clear filters" };
        clear.Click += async (_, _) =>
        {
            _host.Text = string.Empty;
            _plugin.Text = string.Empty;
            _search.Text = string.Empty;
            _source.SelectedIndex = 0;
            _status.SelectedIndex = 0;
            _privilege.SelectedIndex = 0;
            _risk.SelectedIndex = 0;
            _from.Text = string.Empty;
            _to.Text = string.Empty;
            _diagnostics.IsChecked = false;
            await RefreshAsync();
        };
        var filters = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = double.NaN,
            ItemHeight = double.NaN
        };
        filters.Children.Add(_host);
        filters.Children.Add(_plugin);
        filters.Children.Add(_search);
        filters.Children.Add(_source);
        filters.Children.Add(_status);
        filters.Children.Add(_privilege);
        filters.Children.Add(_risk);
        filters.Children.Add(_from);
        filters.Children.Add(_to);
        filters.Children.Add(_diagnostics);
        filters.Children.Add(apply);
        filters.Children.Add(clear);
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            _activityLoadCancellation?.Cancel();
            _activityLoadCancellation?.Dispose();
            _activityLoadCancellation = null;
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { _details, _loadMore, close }
        };
        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 10,
            Children =
            {
                filters,
                Place(_filterError, 1),
                Place(_items, 2),
                Place(actions, 3)
            }
        };
        Opened += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!TryReadDate(_from.Text, out var from) ||
            !TryReadDate(_to.Text, out var through))
        {
            _filterError.Text = "Dates must use YYYY-MM-DD.";
            return;
        }

        var before = through?.AddDays(1);
        if (from is not null && before is not null && from >= before)
        {
            _filterError.Text = "The From date must not be after the To date.";
            return;
        }

        _filterError.Text = string.Empty;
        InvocationSource? source = null;
        if (_source.SelectedIndex > 0 &&
            Enum.TryParse<InvocationSource>(
                _source.SelectedItem?.ToString(),
                out var parsedSource))
        {
            source = parsedSource;
        }

        var status = _status.SelectedIndex > 0
            ? _status.SelectedItem?.ToString()
            : null;
        PrivilegeLevel? privilege = null;
        if (_privilege.SelectedIndex > 0 &&
            Enum.TryParse<PrivilegeLevel>(
                _privilege.SelectedItem?.ToString(),
                out var parsedPrivilege))
        {
            privilege = parsedPrivilege;
        }

        OperationRisk? risk = null;
        if (_risk.SelectedIndex > 0 &&
            Enum.TryParse<OperationRisk>(
                _risk.SelectedItem?.ToString(),
                out var parsedRisk))
        {
            risk = parsedRisk;
        }

        try
        {
            _activityLoadCancellation?.Cancel();
            _activityLoadCancellation?.Dispose();
            _activityLoadCancellation = new();
            _activeFilter = new ActivityFilterViewModel(
                _host.Text,
                _plugin.Text,
                source,
                status,
                privilege,
                risk,
                from,
                before,
                _diagnostics.IsChecked == true,
                _search.Text);
            _cursor = new(SnapshotBefore: DateTimeOffset.UtcNow.AddTicks(1));
            _loadedItems.Clear();
            await LoadNextPageAsync();
        }
        catch (Exception)
        {
            _items.ItemsSource = new[]
            {
                new ActivityItemViewModel(
                    null,
                    Guid.Empty,
                    DateTimeOffset.Now,
                    "unavailable",
                    "unknown",
                    "orvian",
                    "activity",
                    string.Empty,
                    "Local",
                    "Unavailable",
                    "Activity history could not be loaded.",
                    "Local activity")
            };
        }
    }

    private async Task LoadNextPageAsync()
    {
        if (_activeFilter is null)
        {
            return;
        }

        var loadCancellation =
            _activityLoadCancellation ??= new CancellationTokenSource();
        var filter = _activeFilter;
        var cursor = _cursor;
        _loadMore.IsEnabled = false;
        try
        {
            var page = await _viewModel.LoadActivityPageAsync(
                filter,
                cursor,
                cancellationToken: loadCancellation.Token);
            if (!ReferenceEquals(loadCancellation, _activityLoadCancellation))
            {
                return;
            }

            _loadedItems.AddRange(page.Items);
            _items.ItemsSource = _loadedItems.ToArray();
            if (page.NextCursor is not null)
            {
                _cursor = page.NextCursor;
            }

            _loadMore.IsEnabled = page.HasMore;
        }
        catch (OperationCanceledException)
            when (loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _filterError.Text =
                "The next Activity page could not be loaded. Existing results were preserved.";
        }
    }

    private static bool TryReadDate(
        string? value,
        out DateTimeOffset? result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = null;
            return true;
        }

        if (DateTime.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            result = new DateTimeOffset(
                DateTime.SpecifyKind(date, DateTimeKind.Local));
            return true;
        }

        result = null;
        return false;
    }

    private static T Place<T>(T control, int row)
        where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}

internal sealed class CommandAuditDetailWindow : Window
{
    public CommandAuditDetailWindow(CommandAuditDetailViewModel detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        Title = "Command audit details";
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();
        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 10,
            Children =
            {
                new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = detail.Content,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = FontFamily.Default
                    }
                },
                Place(close, 1)
            }
        };
    }

    private static T Place<T>(T control, int row)
        where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ComboBox _appearance = new()
    {
        ItemsSource = Enum.GetValues<AppearancePreference>()
    };
    private readonly TextBox _culture = new()
    {
        PlaceholderText = "System default (for example en-US)"
    };
    private readonly NumericUpDown _connectionTimeout = new()
    {
        Minimum = 1,
        Maximum = 120
    };
    private readonly NumericUpDown _reconnectAttempts = new()
    {
        Minimum = 0,
        Maximum = 5
    };
    private readonly NumericUpDown _outputRetention = new()
    {
        Minimum = 1,
        Maximum = 3650
    };
    private readonly NumericUpDown _auditRetention = new()
    {
        Minimum = 1,
        Maximum = 3650
    };
    private readonly CheckBox _clearCredentials = new()
    {
        Content = "Clear session credentials when disconnecting"
    };
    private readonly TextBlock _status = new()
    {
        Foreground = Brushes.IndianRed,
        TextWrapping = TextWrapping.Wrap
    };

    public SettingsWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Settings";
        Width = 620;
        Height = 650;
        MinWidth = 520;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var form = new StackPanel { Spacing = 10 };
        AddField(form, "Appearance", _appearance);
        AddField(form, "Formatting culture", _culture);
        AddField(form, "Default connection timeout (seconds)", _connectionTimeout);
        AddField(form, "Default additional reconnect attempts", _reconnectAttempts);
        AddField(form, "Captured output retention (days)", _outputRetention);
        AddField(form, "Audit metadata retention (days)", _auditRetention);
        form.Children.Add(_clearCredentials);
        form.Children.Add(new TextBlock
        {
            Text =
                "Host identity verification and mutation auditing remain mandatory and cannot be disabled.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });

        var managePlugins = new Button
        {
            Content = "Manage plugins",
            IsEnabled = viewModel.CanManagePlugins,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        managePlugins.Click += async (_, _) =>
            await new PluginManagementWindow(viewModel).ShowDialog(this);
        var viewDiagnostics = new Button
        {
            Content = "View application diagnostics",
            IsEnabled = viewModel.CanViewDiagnostics,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        viewDiagnostics.Click += async (_, _) =>
            await new DiagnosticsWindow(viewModel).ShowDialog(this);
        var cleanAudit = new Button
        {
            Content = "Run retention cleanup now",
            IsEnabled = viewModel.CanRunAuditRetention,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        cleanAudit.Click += async (_, _) =>
        {
            cleanAudit.IsEnabled = false;
            _status.Text = string.Empty;
            try
            {
                _status.Foreground = Brushes.ForestGreen;
                _status.Text = await viewModel.RunAuditRetentionAsync();
            }
            catch (Exception)
            {
                _status.Foreground = Brushes.IndianRed;
                _status.Text =
                    "Retention cleanup could not be completed. Audit integrity was preserved.";
            }
            finally
            {
                cleanAudit.IsEnabled = viewModel.CanRunAuditRetention;
            }
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = "Save settings" };
        save.Click += async (_, _) => await SaveAsync(save);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, save }
        };

        Content = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "Application settings",
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold
                },
                new ScrollViewer { Content = form },
                managePlugins,
                viewDiagnostics,
                cleanAudit,
                _status,
                actions
            }
        };
        for (var index = 1; index < ((Grid)Content).Children.Count; index++)
        {
            Grid.SetRow(((Grid)Content).Children[index], index);
        }

        LoadValues(viewModel.ApplicationSettings);
    }

    private void LoadValues(ApplicationSettings settings)
    {
        _appearance.SelectedItem = settings.Appearance;
        _culture.Text = settings.CultureName;
        _connectionTimeout.Value =
            (decimal)settings.DefaultConnectionTimeout.TotalSeconds;
        _reconnectAttempts.Value = settings.DefaultMaximumReconnectAttempts;
        _outputRetention.Value = settings.OutputRetentionDays;
        _auditRetention.Value = settings.AuditRetentionDays;
        _clearCredentials.IsChecked = settings.ClearSessionCredentialsOnDisconnect;
    }

    private async Task SaveAsync(Button save)
    {
        _status.Text = string.Empty;
        save.IsEnabled = false;
        try
        {
            if (_appearance.SelectedItem is not AppearancePreference appearance ||
                _connectionTimeout.Value is null ||
                _reconnectAttempts.Value is null ||
                _outputRetention.Value is null ||
                _auditRetention.Value is null)
            {
                _status.Text = "All numeric settings and appearance are required.";
                return;
            }

            var settings = new ApplicationSettings(
                appearance,
                string.IsNullOrWhiteSpace(_culture.Text) ? null : _culture.Text.Trim(),
                TimeSpan.FromSeconds(decimal.ToInt32(_connectionTimeout.Value.Value)),
                decimal.ToInt32(_reconnectAttempts.Value.Value),
                decimal.ToInt32(_outputRetention.Value.Value),
                decimal.ToInt32(_auditRetention.Value.Value),
                _clearCredentials.IsChecked == true);
            await _viewModel.SaveSettingsAsync(settings);
            Close();
        }
        catch (ArgumentException)
        {
            _status.Text =
                "Settings are invalid. Output retention cannot exceed audit retention, and the culture must be valid.";
        }
        catch (Exception)
        {
            _status.Text =
                "Settings could not be saved. Existing persisted settings remain unchanged.";
        }
        finally
        {
            save.IsEnabled = true;
        }
    }

    private static void AddField(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(control);
    }
}

internal sealed class DiagnosticsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly StackPanel _items = new() { Spacing = 10 };

    public DiagnosticsWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Application diagnostics";
        Width = 760;
        Height = 600;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();
        Content = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Application diagnostics",
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text =
                        "Local structured events are separate from remote command audit records. Values shown here are sanitized.",
                    TextWrapping = TextWrapping.Wrap
                },
                new ScrollViewer { Content = _items },
                close
            }
        };
        for (var index = 1; index < ((Grid)Content).Children.Count; index++)
        {
            Grid.SetRow(((Grid)Content).Children[index], index);
        }

        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _items.Children.Clear();
        var diagnostics = await _viewModel.LoadDiagnosticsAsync();
        if (diagnostics.Count == 0)
        {
            _items.Children.Add(new TextBlock
            {
                Text = "No application diagnostics are available."
            });
            return;
        }

        foreach (var item in diagnostics)
        {
            _items.Children.Add(new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = item.Summary,
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = item.Details,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            });
        }
    }
}

internal sealed class PluginManagementWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly StackPanel _items;
    private readonly TextBlock _status;

    public PluginManagementWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Plugin management";
        Width = 680;
        Height = 560;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _items = new StackPanel { Spacing = 12 };
        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed
        };
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(24),
            RowSpacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Installed plugins",
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold
                },
                new ScrollViewer { Content = _items },
                _status,
                close
            }
        };
        Grid.SetRow((Control)((Grid)Content).Children[1], 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(close, 3);
        Opened += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _items.Children.Clear();
        _status.Text = string.Empty;
        var plugins = await _viewModel.GetPluginsAsync();
        if (plugins.Count == 0)
        {
            _items.Children.Add(new TextBlock
            {
                Text = "No plugins were discovered.",
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var plugin in plugins)
        {
            _items.Children.Add(BuildPluginCard(plugin));
        }
    }

    private Control BuildPluginCard(
        Orvian.Application.Plugins.PluginManagementItem plugin)
    {
        var toggle = new Button
        {
            Content = plugin.IsEnabled ? "Disable" : "Enable",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        toggle.Click += async (_, _) =>
        {
            toggle.IsEnabled = false;
            _status.Text = string.Empty;
            try
            {
                await _viewModel.SetPluginEnabledAsync(
                    plugin.PluginId,
                    !plugin.IsEnabled);
                await RefreshAsync();
            }
            catch (Orvian.Application.Plugins.PluginStateChangeException exception)
            {
                _status.Text = exception.SafeMessage;
                toggle.IsEnabled = true;
            }
            catch (Exception)
            {
                _status.Text =
                    "The plugin state could not be changed. No sensitive details were shown.";
                toggle.IsEnabled = true;
            }
        };

        var diagnostics = plugin.Diagnostics.IsDefaultOrEmpty
            ? "No reported errors."
            : string.Join(
                Environment.NewLine,
                plugin.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
        var permissions = plugin.Permissions.IsDefaultOrEmpty
            ? "None"
            : string.Join(", ", plugin.Permissions);
        return new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = plugin.Name,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 16
                    },
                    new TextBlock
                    {
                        Text =
                            $"{plugin.Publisher} · {plugin.Version} · {plugin.LifecycleState}"
                    },
                    new TextBlock
                    {
                        Text = $"Permissions: {permissions}",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = diagnostics,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12
                    },
                    toggle
                }
            }
        };
    }
}

internal sealed class FeatureResultView : UserControl
{
    private readonly Window _owner;

    public FeatureResultView(
        Window owner,
        PluginFeatureDisplay feature,
        string featureId,
        MainWindowViewModel viewModel,
        Action navigateBack)
    {
        _owner = owner;

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(new TextBlock
        {
            Text = feature.Title,
            FontSize = 26,
            FontWeight = FontWeight.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = viewModel.SelectedHost is null
                ? "No host selected"
                : $"{viewModel.SelectedHost.DisplayName}  ·  " +
                  viewModel.SelectedHost.Endpoint,
            FontSize = 13,
            Opacity = 0.62
        });
        header.Children.Add(heading);
        var mode = new Border
        {
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(28, 91, 108, 255)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = feature.Mutations.IsDefaultOrEmpty
                    ? "READ ONLY"
                    : "MANAGE",
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#9AA5FF"))
            }
        };
        Grid.SetColumn(mode, 1);
        header.Children.Add(mode);

        var body = new StackPanel { Spacing = 18 };
        if (feature.IsError)
        {
            body.Children.Add(BuildFeatureMessageCard(
                "Feature unavailable",
                feature.Content,
                isError: true));
        }
        else if (feature.Mutations.IsDefaultOrEmpty)
        {
            body.Children.Add(BuildFeatureDataView(feature));
        }

        if (!feature.Mutations.IsDefaultOrEmpty)
        {
            var mutationIndex = body.Children.Count;
            Func<Task>? refreshMutation = null;
            refreshMutation = async () =>
            {
                var refreshed = await viewModel.LoadFeatureAsync(featureId, feature.Title);
                body.Children[mutationIndex] = refreshed.IsError
                    ? BuildFeatureMessageCard("Refresh failed", refreshed.Content, true)
                    : BuildMutationCard(refreshed, featureId, viewModel, refreshMutation!);
            };
            body.Children.Add(BuildMutationCard(
                feature,
                featureId,
                viewModel,
                refreshMutation));
        }

        var back = new Button
        {
            Content = "Back",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 8),
            CornerRadius = new CornerRadius(9)
        };
        AutomationProperties.SetName(back, "Back to host overview");
        back.Click += (_, _) => navigateBack();

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(42, 40, 42, 32),
            RowSpacing = 18
        };
        layout.Children.Add(header);
        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid.SetRow(back, 2);
        layout.Children.Add(back);
        Content = layout;
    }

    private static Control BuildFeatureDataView(PluginFeatureDisplay feature)
    {
        var content = new StackPanel { Spacing = 0 };
        if (feature.Values.IsDefaultOrEmpty)
        {
            content.Children.Add(new TextBlock
            {
                Text = feature.Content,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            });
        }
        else
        {
            for (var index = 0; index < feature.Values.Length; index++)
            {
                var value = feature.Values[index];
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("180,*"),
                    MinHeight = 38,
                    Margin = new Thickness(0, 2)
                };
                row.Children.Add(new TextBlock
                {
                    Text = value.Label,
                    Opacity = 0.65,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                });
                var valueText = new TextBlock
                {
                    Text = value.Value,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(valueText, 1);
                row.Children.Add(valueText);
                content.Children.Add(row);
                if (index < feature.Values.Length - 1)
                {
                    content.Children.Add(new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(
                            Color.FromArgb(26, 128, 128, 128))
                    });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(feature.ProviderId))
        {
            content.Children.Add(new TextBlock
            {
                Text = $"Provider  ·  {feature.ProviderId}",
                FontSize = 11,
                Opacity = 0.5,
                Margin = new Thickness(0, 12, 0, 0)
            });
        }

        return content;
    }

    private Control BuildMutationCard(
        PluginFeatureDisplay feature,
        string featureId,
        MainWindowViewModel viewModel,
        Func<Task> refreshFeature)
    {
        if (string.Equals(featureId, "services", StringComparison.Ordinal))
        {
            return BuildServicesView(feature, featureId, viewModel, refreshFeature);
        }

        if (string.Equals(featureId, "docker", StringComparison.Ordinal))
        {
            return BuildDockerView(feature, featureId, viewModel, refreshFeature);
        }

        if (string.Equals(featureId, "network-settings", StringComparison.Ordinal))
        {
            return BuildNetworkMutationTabs(
                feature,
                featureId,
                viewModel,
                refreshFeature);
        }

        if (string.Equals(featureId, "date-time", StringComparison.Ordinal) &&
            feature.Mutations.Length > 1)
        {
            return BuildDateTimeMutationTabs(
                feature,
                featureId,
                viewModel,
                refreshFeature);
        }

        var selectedMutation = feature.Mutations[0];
        var actions = new TabStrip
        {
            ItemsSource = feature.Mutations,
            SelectedItem = selectedMutation,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        actions.ItemTemplate = new FuncDataTemplate<PluginMutationAction>(
            (item, _) => new TextBlock { Text = item?.Title });
        var purpose = new TextBlock
        {
            Text = selectedMutation.Purpose,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68,
            LineHeight = 19
        };
        var inputs = new StackPanel { Spacing = 10 };
        var inputControls = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        void RebuildInputs(PluginMutationAction mutation)
        {
            inputs.Children.Clear();
            inputControls.Clear();
            foreach (var parameter in mutation.Parameters)
            {
                var input = new TextBox
                {
                    PlaceholderText = parameter.Label,
                    CornerRadius = new CornerRadius(8),
                    MinHeight = 38
                };
                inputControls.Add(parameter.Name, input);
                inputs.Children.Add(input);
            }
        }
        RebuildInputs(selectedMutation);
        var outcome = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        var apply = new Button
        {
            Content = selectedMutation.Title,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 9),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.Parse("#5B6CFF")),
            Foreground = Brushes.White
        };
        actions.SelectionChanged += (_, _) =>
        {
            if (actions.SelectedItem is not PluginMutationAction selected)
            {
                return;
            }

            selectedMutation = selected;
            RebuildInputs(selected);
            purpose.Text = selected.Purpose;
            apply.Content = selected.Title;
            outcome.Text = string.Empty;
            outcome.IsVisible = false;
        };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                if (await viewModel.NeedsSelectedHostPrivilegeCredentialAsync(
                        selectedMutation))
                {
                    var credential = await new PrivilegeCredentialWindow()
                        .ShowDialog<char[]?>(_owner);
                    if (credential is null)
                    {
                        outcome.Text =
                            "A privilege credential is required to apply this change.";
                        outcome.Foreground =
                            new SolidColorBrush(Color.Parse("#FFB454"));
                        outcome.IsVisible = true;
                        return;
                    }

                    try
                    {
                        viewModel.SetSelectedHostPrivilegeCredential(credential);
                    }
                    finally
                    {
                        Array.Clear(credential);
                    }
                }

                var result = await viewModel.ExecuteFeatureMutationAsync(
                    featureId,
                    selectedMutation,
                    inputControls.ToDictionary(
                        item => item.Key,
                        item => item.Value.Text ?? string.Empty,
                        StringComparer.Ordinal));
                if (result.IsSuccess)
                {
                    await refreshFeature();
                }

                outcome.Text = result.Message;
                outcome.Foreground = result.IsSuccess
                    ? new SolidColorBrush(Color.Parse("#57C98B"))
                    : new SolidColorBrush(Color.Parse("#FF6B6B"));
                outcome.IsVisible = true;
            }
            finally
            {
                apply.IsEnabled = true;
            }
        };

        var form = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                actions,
                purpose,
                inputs,
                apply,
                outcome
            }
        };
        return form;
    }

    private Control BuildServicesView(
        PluginFeatureDisplay feature,
        string featureId,
        MainWindowViewModel viewModel,
        Func<Task> refreshFeature)
    {
        var services = ParseServices(feature);
        var search = new TextBox
        {
            PlaceholderText = "Search services by name or description",
            MinHeight = 40,
            CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(search, "Search services");
        var stateFilter = new ComboBox
        {
            ItemsSource = new[] { "All services", "Running", "Stopped", "Enabled", "Disabled" },
            SelectedIndex = 0,
            MinWidth = 150,
            MinHeight = 40,
            CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(stateFilter, "Filter services by state");
        var summary = new TextBlock
        {
            FontSize = 13,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center
        };
        var rows = new StackPanel { Spacing = 8 };
        var outcome = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        var actions = feature.Mutations.ToDictionary(
            mutation => mutation.MutationId,
            StringComparer.Ordinal);

        async Task ExecuteAsync(ServiceFeatureRow service, string mutationId, Button button)
        {
            if (!actions.TryGetValue(mutationId, out var mutation))
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                if (!await EnsurePrivilegeCredentialAsync(viewModel, mutation, outcome))
                {
                    return;
                }

                var result = await viewModel.ExecuteFeatureMutationAsync(
                    featureId, mutation, service.Id);
                ShowMutationOutcome(outcome, result.IsSuccess, result.Message);
                if (result.IsSuccess)
                {
                    await refreshFeature();
                }
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        Button ActionButton(ServiceFeatureRow service, string mutationId, string label)
        {
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(12, 6),
                CornerRadius = new CornerRadius(7),
                MinWidth = 70,
                Margin = new Thickness(3)
            };
            AutomationProperties.SetName(button, $"{label} {service.Id}");
            button.Click += async (_, _) => await ExecuteAsync(service, mutationId, button);
            return button;
        }

        Control ServiceRow(ServiceFeatureRow service)
        {
            var actionPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (string.Equals(service.ActiveState, "active", StringComparison.Ordinal))
            {
                actionPanel.Children.Add(ActionButton(service, "service.stop", "Stop"));
                actionPanel.Children.Add(ActionButton(service, "service.restart", "Restart"));
            }
            else
            {
                actionPanel.Children.Add(ActionButton(service, "service.start", "Start"));
            }

            if (string.Equals(service.UnitFileState, "enabled", StringComparison.Ordinal) ||
                string.Equals(service.UnitFileState, "enabled-runtime", StringComparison.Ordinal))
            {
                actionPanel.Children.Add(ActionButton(service, "service.disable", "Disable"));
            }
            else if (string.Equals(service.UnitFileState, "disabled", StringComparison.Ordinal))
            {
                actionPanel.Children.Add(ActionButton(service, "service.enable", "Enable"));
            }

            var details = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = service.Id,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = service.Description,
                        FontSize = 12,
                        Opacity = 0.65,
                        TextWrapping = TextWrapping.Wrap,
                        IsVisible = !string.IsNullOrWhiteSpace(service.Description)
                    },
                    new TextBlock
                    {
                        Text = $"{ServiceStateLabel(service)}  ·  {service.UnitFileState}",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse(
                            service.ActiveState == "active" ? "#57C98B" : "#9AA0AA"))
                    }
                }
            };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 16,
                Children = { details, actionPanel }
            };
            Grid.SetColumn(actionPanel, 1);
            return new Border
            {
                Padding = new Thickness(14, 12),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                Child = grid
            };
        }

        void RebuildRows()
        {
            var term = search.Text?.Trim() ?? string.Empty;
            var filter = stateFilter.SelectedItem as string ?? "All services";
            var filtered = services.Where(service =>
                    (term.Length == 0 || service.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                     service.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) &&
                    MatchesServiceFilter(service, filter))
                .ToArray();
            rows.Children.Clear();
            summary.Text = $"Showing {filtered.Length:N0} of {services.Length:N0} services";
            if (filtered.Length == 0)
            {
                rows.Children.Add(BuildFeatureMessageCard(
                    "No matching services",
                    "Try a different search term or state filter.",
                    false));
                return;
            }

            foreach (var service in filtered)
            {
                rows.Children.Add(ServiceRow(service));
            }
        }

        search.TextChanged += (_, _) => RebuildRows();
        stateFilter.SelectionChanged += (_, _) => RebuildRows();
        RebuildRows();

        var filters = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Children = { search, stateFilter }
        };
        Grid.SetColumn(stateFilter, 1);
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                filters,
                summary,
                outcome,
                rows,
                new TextBlock
                {
                    Text = $"Provider  ·  {feature.ProviderId}",
                    FontSize = 11,
                    Opacity = 0.5,
                    Margin = new Thickness(0, 4, 0, 0)
                }
            }
        };
    }

    private static ImmutableArray<ServiceFeatureRow> ParseServices(
        PluginFeatureDisplay feature)
    {
        return feature.Values
            .Where(value => value.Key.StartsWith("Service ", StringComparison.Ordinal) &&
                value.Key.EndsWith(".Id", StringComparison.Ordinal))
            .Select(value =>
            {
                var prefix = value.Key[..^3];
                return new ServiceFeatureRow(
                    value.Value,
                    FeatureValue(feature, $"{prefix}.Description"),
                    FeatureValue(feature, $"{prefix}.LoadState"),
                    FeatureValue(feature, $"{prefix}.ActiveState"),
                    FeatureValue(feature, $"{prefix}.SubState"),
                    FeatureValue(feature, $"{prefix}.UnitFileState"));
            })
            .OrderBy(service => service.Id, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static bool MatchesServiceFilter(ServiceFeatureRow service, string filter) =>
        filter switch
        {
            "Running" => service.ActiveState == "active",
            "Stopped" => service.ActiveState != "active",
            "Enabled" => service.UnitFileState is "enabled" or "enabled-runtime",
            "Disabled" => service.UnitFileState == "disabled",
            _ => true
        };

    private static string ServiceStateLabel(ServiceFeatureRow service) =>
        service.ActiveState == "active"
            ? $"Running ({service.SubState})"
            : service.ActiveState == "failed"
                ? $"Failed ({service.SubState})"
                : $"Stopped ({service.SubState})";

    private sealed record ServiceFeatureRow(
        string Id,
        string Description,
        string LoadState,
        string ActiveState,
        string SubState,
        string UnitFileState);

    private Control BuildDockerView(
        PluginFeatureDisplay feature,
        string featureId,
        MainWindowViewModel viewModel,
        Func<Task> refreshFeature)
    {
        var images = DockerFeatureProjection.ParseImages(feature);
        var containers = DockerFeatureProjection.ParseContainers(feature);
        var actions = feature.Mutations.ToDictionary(
            mutation => mutation.MutationId,
            StringComparer.Ordinal);
        var readActions = feature.ReadActions.ToDictionary(
            action => action.ActionId,
            StringComparer.Ordinal);
        var page = new ContentControl();
        StackPanel? overview = null;
        var outcome = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            MaxHeight = 320
        };

        async Task ExecuteAsync(
            string mutationId,
            IReadOnlyDictionary<string, string> parameters,
            Button button)
        {
            if (!actions.TryGetValue(mutationId, out var mutation))
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                if (!await EnsurePrivilegeCredentialAsync(viewModel, mutation, outcome))
                {
                    return;
                }

                var result = await viewModel.ExecuteFeatureMutationAsync(
                    featureId,
                    mutation,
                    parameters);
                ShowMutationOutcome(outcome, result.IsSuccess, result.Message);
                if (result.IsSuccess && !mutation.DisplaysOutput)
                {
                    await refreshFeature();
                }
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        Button RowAction(
            string mutationId,
            string label,
            string parameterName,
            string value)
        {
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(11, 6),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(3)
            };
            AutomationProperties.SetName(button, $"{label} {value}");
            button.Click += async (_, _) => await ExecuteAsync(
                mutationId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [parameterName] = value
                },
                button);
            return button;
        }

        Button LogsButton(DockerContainerFeatureRow container)
        {
            var button = new Button
            {
                Content = "Logs",
                Padding = new Thickness(11, 6),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(3)
            };
            button.Click += (_, _) =>
            {
                if (readActions.TryGetValue("container.logs", out var action))
                {
                    page.Content = BuildDockerLogsPage(
                        featureId, container, action, viewModel,
                        () => page.Content = overview);
                }
            };
            return button;
        }

        Control ImageRow(DockerImageFeatureRow image)
        {
            var reference = image.Reference;
            var buttons = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Children =
                {
                    RowAction("image.remove", "Delete", "image", image.Id)
                }
            };
            var details = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = reference,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = $"{image.Size}  ·  {image.CreatedSince}",
                        FontSize = 12,
                        Opacity = 0.65
                    },
                    new TextBlock
                    {
                        Text = image.Id,
                        FontFamily = new FontFamily("monospace"),
                        FontSize = 11,
                        Opacity = 0.55,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            };
            return DockerRow(details, buttons);
        }

        Control ContainerRow(DockerContainerFeatureRow container)
        {
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            if (container.IsRunning)
            {
                buttons.Children.Add(RowAction(
                    "container.stop", "Stop", "container", container.Identifier));
                buttons.Children.Add(RowAction(
                    "container.restart", "Restart", "container", container.Identifier));
            }
            else
            {
                buttons.Children.Add(RowAction(
                    "container.start", "Start", "container", container.Identifier));
            }

            buttons.Children.Add(LogsButton(container));
            buttons.Children.Add(RowAction(
                "container.remove", "Delete", "container", container.Identifier));
            var details = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = container.DisplayName,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = container.Image,
                        FontSize = 12,
                        Opacity = 0.65,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = $"{container.Status}  ·  {container.Ports}",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse(
                            container.IsRunning ? "#57C98B" : "#9AA0AA")),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            };
            return DockerRow(details, buttons);
        }

        Control BuildList<T>(
            ImmutableArray<T> items,
            string emptyTitle,
            string emptyMessage,
            Func<T, Control> rowFactory)
        {
            if (items.IsDefaultOrEmpty)
            {
                return BuildFeatureMessageCard(emptyTitle, emptyMessage, false);
            }

            var rows = new StackPanel { Spacing = 8 };
            foreach (var item in items)
            {
                rows.Children.Add(rowFactory(item));
            }

            return new ScrollViewer
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = rows
            };
        }

        var pullInput = new TextBox
        {
            PlaceholderText = "Image reference, for example nginx:latest",
            MinHeight = 40,
            CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(pullInput, "Docker image reference");
        var pull = new Button
        {
            Content = "Pull image",
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(8)
        };
        pull.Click += async (_, _) => await ExecuteAsync(
            "image.pull",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = pullInput.Text ?? string.Empty
            },
            pull);
        var pullPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Children = { pullInput, pull }
        };
        Grid.SetColumn(pull, 1);

        var prune = new Button
        {
            Content = "Prune unused data",
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        prune.Click += async (_, _) => await ExecuteAsync(
            "system.prune",
            new Dictionary<string, string>(StringComparer.Ordinal),
            prune);

        overview = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                pullPanel,
                outcome,
                new TabControl
                {
                    ItemsSource = new[]
                    {
                        new TabItem
                        {
                            Header = $"Containers ({containers.Length:N0})",
                            Content = BuildList(
                                containers,
                                "No containers",
                                "Docker has no containers on this host.",
                                ContainerRow)
                        },
                        new TabItem
                        {
                            Header = $"Images ({images.Length:N0})",
                            Content = BuildList(
                                images,
                                "No images",
                                "Docker has no images on this host.",
                                ImageRow)
                        }
                    }
                },
                new TextBlock
                {
                    Text = "Maintenance",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 8, 0, 0)
                },
                new TextBlock
                {
                    Text = "Prune stopped containers, unused networks, dangling images, and build cache.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65
                },
                prune,
                new TextBlock
                {
                    Text = $"Provider  ·  {feature.ProviderId}",
                    FontSize = 11,
                    Opacity = 0.5
                }
            }
        };
        page.Content = overview;
        return page;
    }

    private Control BuildDockerLogsPage(
        string featureId,
        DockerContainerFeatureRow container,
        PluginReadAction action,
        MainWindowViewModel viewModel,
        Action goBack)
    {
        const int maximumCharacters = 128 * 1024;
        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            Height = 420,
            MinHeight = 240,
            MaxHeight = 420,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        output.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto);
        output.SetValue(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto);
        var status = new TextBlock { Opacity = 0.65 };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        var lifetime = new CancellationTokenSource();
        var loading = false;

        async Task RefreshAsync()
        {
            if (loading) return;
            loading = true;
            try
            {
                var result = await viewModel.ExecuteFeatureReadActionAsync(
                    featureId, action,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container"] = container.Identifier
                    },
                    lifetime.Token);
                if (result.IsSuccess)
                {
                    output.Text = result.Message.Length <= maximumCharacters
                        ? result.Message
                        : result.Message[^maximumCharacters..];
                    output.CaretIndex = output.Text?.Length ?? 0;
                    status.Text = $"Live polling  ·  updated {DateTimeOffset.Now:t}";
                }
                else status.Text = result.Message;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { loading = false; }
        }

        timer.Tick += async (_, _) => await RefreshAsync();
        var back = new Button { Content = "← Containers" };
        back.Click += (_, _) => { timer.Stop(); lifetime.Cancel(); goBack(); };
        var pause = new Button { Content = "Pause" };
        pause.Click += (_, _) => { timer.Stop(); status.Text = "Paused"; };
        var resume = new Button { Content = "Resume" };
        resume.Click += async (_, _) => { timer.Start(); await RefreshAsync(); };
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await RefreshAsync();
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => output.Text = string.Empty;
        var controls = new WrapPanel { Children = { back, pause, resume, refresh, clear } };
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Logs · {container.DisplayName}", FontSize = 20,
                    FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = container.Image, Opacity = 0.65 },
                controls, status, output
            }
        };
        panel.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            lifetime.Cancel();
        };
        timer.Start();
        _ = RefreshAsync();
        return panel;
    }

    private static Border DockerRow(Control details, Control actions)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 16,
            Children = { details, actions }
        };
        Grid.SetColumn(actions, 1);
        return new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
            Child = grid
        };
    }

    private Control BuildNetworkMutationTabs(
        PluginFeatureDisplay feature,
        string featureId,
        MainWindowViewModel viewModel,
        Func<Task> refreshFeature)
    {
        var tabs = new TabControl
        {
            ItemsSource = feature.Mutations.Select(mutation => new TabItem
            {
                Header = mutation.Title,
                Content = mutation.Parameters.Any(parameter => parameter.Name == "interface")
                    ? BuildNetplanMutationForm(feature, mutation, featureId, viewModel,
                        refreshFeature)
                    : BuildGenericTabbedMutationForm(mutation, featureId, viewModel,
                        refreshFeature)
            }).ToArray()
        };
        return tabs;
    }

    private Control BuildNetplanMutationForm(
        PluginFeatureDisplay feature,
        PluginMutationAction mutation,
        string featureId,
        MainWindowViewModel viewModel,
        Func<Task> refreshFeature)
    {
        var interfaces = feature.Values
            .Where(value => value.Key.EndsWith(".ipv4_addresses", StringComparison.Ordinal) ||
                value.Key.EndsWith(".ipv6_addresses", StringComparison.Ordinal))
            .Select(value => value.Key[..value.Key.IndexOf('.', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var interfaceSelector = new ComboBox
        {
            ItemsSource = interfaces,
            SelectedIndex = interfaces.Length > 0 ? 0 : -1,
            PlaceholderText = "Select a network interface",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            MinHeight = 42
        };
        var ipv4 = NetworkTextBox("IPv4 address");
        var ipv4Prefix = new NumericUpDown
        {
            Minimum = 0, Maximum = 32, Value = 24, FormatString = "0",
            Width = 138, MinWidth = 138, MinHeight = 42,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var ipv4Gateway = NetworkTextBox("IPv4 gateway");
        var ipv6 = NetworkTextBox("IPv6 address");
        var ipv6Prefix = new NumericUpDown
        {
            Minimum = 0, Maximum = 128, Value = 64, FormatString = "0",
            Width = 138, MinWidth = 138, MinHeight = 42,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var ipv6Gateway = NetworkTextBox("IPv6 gateway");
        var dns = NetworkTextBox("DNS servers, separated by commas");
        var outcome = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var apply = new Button
        {
            Content = mutation.Title,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 9),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.Parse("#5B6CFF")),
            Foreground = Brushes.White
        };

        void LoadCurrentValues()
        {
            if (interfaceSelector.SelectedItem is not string device)
            {
                return;
            }

            LoadCidr(feature, $"{device}.ipv4_addresses", ipv4, ipv4Prefix);
            LoadCidr(feature, $"{device}.ipv6_addresses", ipv6, ipv6Prefix);
            ipv4Gateway.Text = FeatureValue(feature, $"{device}.ipv4_gateway");
            ipv6Gateway.Text = FeatureValue(feature, $"{device}.ipv6_gateway");
            dns.Text = FeatureValue(feature, "dns_servers");
        }

        interfaceSelector.SelectionChanged += (_, _) => LoadCurrentValues();
        LoadCurrentValues();
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                if (interfaceSelector.SelectedItem is not string device)
                {
                    ShowMutationOutcome(outcome, false, "Select a network interface.");
                    return;
                }

                if (!await EnsurePrivilegeCredentialAsync(viewModel, mutation, outcome))
                {
                    return;
                }

                var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["interface"] = device,
                    ["ipv4Address"] = BuildCidr(ipv4.Text, ipv4Prefix.Value),
                    ["ipv4Gateway"] = ipv4Gateway.Text ?? string.Empty,
                    ["ipv6Address"] = BuildCidr(ipv6.Text, ipv6Prefix.Value),
                    ["ipv6Gateway"] = ipv6Gateway.Text ?? string.Empty,
                    ["dns"] = dns.Text ?? string.Empty
                };
                var result = await viewModel.ExecuteFeatureMutationAsync(
                    featureId, mutation, parameters);
                if (result.IsSuccess)
                {
                    await refreshFeature();
                }

                ShowMutationOutcome(outcome, result.IsSuccess, result.Message);
            }
            finally
            {
                apply.IsEnabled = true;
            }
        };

        return new StackPanel
        {
            Margin = new Thickness(2, 14, 2, 2),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = mutation.Purpose, TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.68, LineHeight = 19 },
                Field("Network interface", interfaceSelector),
                AddressWithPrefix("IPv4 address", ipv4, ipv4Prefix),
                Field("IPv4 gateway", ipv4Gateway),
                AddressWithPrefix("IPv6 address", ipv6, ipv6Prefix),
                Field("IPv6 gateway", ipv6Gateway),
                Field("DNS servers", dns),
                apply,
                outcome
            }
        };
    }

    private Control BuildGenericTabbedMutationForm(
        PluginMutationAction mutation,
        string featureId,
        MainWindowViewModel viewModel,
        Func<Task> refreshFeature)
    {
        var inputs = mutation.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => new TextBox
            {
                PlaceholderText = parameter.Label,
                MinHeight = 42,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(8)
            }, StringComparer.Ordinal);
        var outcome = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var apply = new Button
        {
            Content = mutation.Title, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 9), CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.Parse("#5B6CFF")),
            Foreground = Brushes.White
        };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                if (!await EnsurePrivilegeCredentialAsync(viewModel, mutation, outcome))
                {
                    return;
                }

                var result = await viewModel.ExecuteFeatureMutationAsync(featureId, mutation,
                    inputs.ToDictionary(item => item.Key,
                        item => item.Value.Text ?? string.Empty, StringComparer.Ordinal));
                if (result.IsSuccess)
                {
                    await refreshFeature();
                }

                ShowMutationOutcome(outcome, result.IsSuccess, result.Message);
            }
            finally
            {
                apply.IsEnabled = true;
            }
        };
        var panel = new StackPanel { Margin = new Thickness(2, 14, 2, 2), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = mutation.Purpose,
            TextWrapping = TextWrapping.Wrap, Opacity = 0.68 });
        foreach (var parameter in mutation.Parameters)
        {
            panel.Children.Add(Field(parameter.Label, inputs[parameter.Name]));
        }
        panel.Children.Add(apply);
        panel.Children.Add(outcome);
        return panel;
    }

    private async Task<bool> EnsurePrivilegeCredentialAsync(
        MainWindowViewModel viewModel,
        PluginMutationAction mutation,
        TextBlock outcome)
    {
        if (!await viewModel.NeedsSelectedHostPrivilegeCredentialAsync(mutation))
        {
            return true;
        }

        var credential = await new PrivilegeCredentialWindow().ShowDialog<char[]?>(_owner);
        if (credential is null)
        {
            ShowMutationOutcome(outcome, false,
                "A privilege credential is required to apply this change.");
            return false;
        }

        try
        {
            viewModel.SetSelectedHostPrivilegeCredential(credential);
            return true;
        }
        finally
        {
            Array.Clear(credential);
        }
    }

    private static Control Field(string label, Control control) => new StackPanel
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = label, FontSize = 12,
                FontWeight = FontWeight.SemiBold, Opacity = 0.68 },
            control
        }
    };

    private static TextBox NetworkTextBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        MinHeight = 42,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(8)
    };

    private static Control AddressWithPrefix(
        string label, TextBox address, NumericUpDown prefix)
    {
        var prefixPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "/",
                    FontSize = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                },
                prefix
            }
        };
        Grid.SetColumn(prefixPanel, 1);
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = label, FontSize = 12,
                    FontWeight = FontWeight.SemiBold, Opacity = 0.68 },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 8,
                    Children = { address, prefixPanel }
                }
            }
        };
    }

    private static void LoadCidr(
        PluginFeatureDisplay feature, string key, TextBox address, NumericUpDown prefix)
    {
        var current = FeatureValue(feature, key);
        var cidr = current.Split(',', StringSplitOptions.TrimEntries)[0];
        var separator = cidr.LastIndexOf('/');
        address.Text = separator > 0 ? cidr[..separator] : string.Empty;
        if (separator > 0 && decimal.TryParse(cidr[(separator + 1)..],
                NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            prefix.Value = value;
        }
    }

    private static string FeatureValue(PluginFeatureDisplay feature, string key) =>
        feature.Values.FirstOrDefault(value => value.Key == key)?.Value is { } value &&
        value != "None" ? value : string.Empty;

    private static string BuildCidr(string? address, decimal? prefix) =>
        string.IsNullOrWhiteSpace(address) ? string.Empty :
            $"{address.Trim()}/{decimal.ToInt32(prefix ?? 0)}";

    private static void ShowMutationOutcome(TextBlock outcome, bool success, string message)
    {
        outcome.Text = message;
        outcome.Foreground = new SolidColorBrush(Color.Parse(success ? "#57C98B" : "#FF6B6B"));
        outcome.IsVisible = true;
    }

    private Control BuildDateTimeMutationTabs(
        PluginFeatureDisplay feature,
        string featureId,
        MainWindowViewModel viewModel,
        Func<Task> refreshFeature)
    {
        var tabs = new TabControl
        {
            ItemsSource = feature.Mutations.Select(mutation => new TabItem
            {
                Header = mutation.Title,
                Content = BuildDateTimeMutationForm(
                    feature,
                    mutation,
                    featureId,
                    viewModel,
                    refreshFeature)
            }).ToArray()
        };
        return tabs;
    }

    private Control BuildDateTimeMutationForm(
        PluginFeatureDisplay feature,
        PluginMutationAction mutation,
        string featureId,
        MainWindowViewModel viewModel,
        Func<Task> refreshFeature)
    {
        var purpose = new TextBlock
        {
            Text = mutation.Purpose,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68,
            LineHeight = 19
        };
        var input = new TextBox
        {
            PlaceholderText = mutation.ParameterLabel,
            CornerRadius = new CornerRadius(8),
            MinHeight = 38
        };
        CalendarDatePicker? datePicker = null;
        TimePicker? timePicker = null;
        Control editor = input;
        if (string.Equals(mutation.ParameterName, "timezone", StringComparison.Ordinal))
        {
            input.Text = FeatureValue(feature, "Timezone");
        }
        else if (string.Equals(mutation.ParameterName, "dateTime", StringComparison.Ordinal))
        {
            datePicker = new CalendarDatePicker
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 42,
                PlaceholderText = "Select date"
            };
            timePicker = new TimePicker
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 42,
                ClockIdentifier = "24HourClock",
                MinuteIncrement = 1,
                SecondIncrement = 1
            };
            AutomationProperties.SetName(datePicker, "Date");
            AutomationProperties.SetName(timePicker, "Time");

            var current = FeatureValue(feature, "TimeUSec");
            if (DateTimeOffset.TryParse(current, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                datePicker.SelectedDate = parsed.Date;
                timePicker.SelectedTime = parsed.TimeOfDay;
            }

            var timeGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 10,
                Children = { datePicker, timePicker }
            };
            Grid.SetColumn(timePicker, 1);
            editor = timeGrid;
        }
        var outcome = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        var apply = new Button
        {
            Content = mutation.Title,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 9),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.Parse("#5B6CFF")),
            Foreground = Brushes.White
        };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                if (await viewModel.NeedsSelectedHostPrivilegeCredentialAsync(mutation))
                {
                    var credential = await new PrivilegeCredentialWindow()
                        .ShowDialog<char[]?>(_owner);
                    if (credential is null)
                    {
                        outcome.Text =
                            "A privilege credential is required to apply this change.";
                        outcome.Foreground =
                            new SolidColorBrush(Color.Parse("#FFB454"));
                        outcome.IsVisible = true;
                        return;
                    }

                    try
                    {
                        viewModel.SetSelectedHostPrivilegeCredential(credential);
                    }
                    finally
                    {
                        Array.Clear(credential);
                    }
                }

                string value;
                if (datePicker is not null && timePicker is not null)
                {
                    if (datePicker.SelectedDate is not { } date ||
                        timePicker.SelectedTime is not { } time)
                    {
                        ShowMutationOutcome(outcome, false, "Select both a date and a time.");
                        return;
                    }

                    value = date.Date.Add(time).ToString(
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture);
                }
                else
                {
                    value = input.Text ?? string.Empty;
                }

                var result = await viewModel.ExecuteFeatureMutationAsync(
                    featureId,
                    mutation,
                    value);
                if (result.IsSuccess)
                {
                    await refreshFeature();
                }

                outcome.Text = result.Message;
                outcome.Foreground = result.IsSuccess
                    ? new SolidColorBrush(Color.Parse("#57C98B"))
                    : new SolidColorBrush(Color.Parse("#FF6B6B"));
                outcome.IsVisible = true;
            }
            finally
            {
                apply.IsEnabled = true;
            }
        };

        return new StackPanel
        {
            Margin = new Thickness(2, 14, 2, 2),
            Spacing = 12,
            Children =
            {
                purpose,
                editor,
                apply,
                outcome
            }
        };
    }

    private static Control BuildFeatureMessageCard(
        string title,
        string message,
        bool isError)
    {
        return CreateFeatureCard(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 16,
                    Foreground = isError
                        ? new SolidColorBrush(Color.Parse("#FF6B6B"))
                        : null
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                }
            }
        });
    }

    private static Border CreateFeatureCard(Control content) => new()
    {
        Padding = new Thickness(20),
        CornerRadius = new CornerRadius(14),
        Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(38, 128, 128, 128)),
        BorderThickness = new Thickness(1),
        Child = content
    };
}
