using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Orvian.Application.Files;
using Orvian.Connections;

namespace Orvian.Shell;

internal sealed record LocalFileItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTimeOffset LastWriteTime)
{
    public string Kind => IsDirectory ? "Folder" : "File";
    public string Size => IsDirectory ? string.Empty : FormatSize(Length);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

internal sealed class FileTransferView : UserControl
{
    private readonly Window _owner;
    private readonly MainWindowViewModel _viewModel;
    private readonly ObservableCollection<LocalFileItem> _localItems = [];
    private readonly ObservableCollection<RemoteFileEntry> _remoteItems = [];
    private readonly TextBox _localPath = new();
    private readonly TextBox _remotePath = new() { Text = "/" };
    private readonly ListBox _localList = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _remoteList = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, IsIndeterminate = false, Value = 0 };
    private readonly Button _cancel = new() { Content = "Cancel operation", IsEnabled = false };
    private CancellationTokenSource? _operationCancellation;
    private ClipboardPayload? _clipboard;
    private PointerPressedEventArgs? _dragStartEvent;
    private Point _dragStartPoint;
    private bool _dragging;
    private readonly List<WatchedRemoteFile> _watchedFiles = [];
    private readonly DispatcherTimer _watchTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private bool _watchCheckRunning;

    public FileTransferView(Window owner, MainWindowViewModel viewModel)
    {
        _owner = owner;
        _viewModel = viewModel;
        _localPath.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _watchTimer.Tick += async (_, _) => await CheckWatchedFilesAsync();
        Content = BuildContent();
        AttachedToVisualTree += async (_, _) => await InitializeAsync();
        DetachedFromVisualTree += (_, _) =>
        {
            CancelOperation();
            _watchTimer.Stop();
        };
    }

    private Control BuildContent()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(22),
            RowSpacing = 12
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = "File Transfer", FontSize = 24, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Drag, copy, or move multiple files between this computer and the selected host.", Opacity = 0.72 }
            }
        });
        _cancel.Click += (_, _) => CancelOperation();
        Grid.SetColumn(_cancel, 1);
        header.Children.Add(_cancel);
        root.Children.Add(header);

        var panes = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            ColumnSpacing = 12
        };
        panes.Children.Add(BuildLocalPane());
        var transferButtons = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };
        var upload = new Button { Content = "Upload  →", MinWidth = 105 };
        upload.Click += async (_, _) => await UploadSelectedAsync();
        var download = new Button { Content = "←  Download", MinWidth = 105 };
        download.Click += async (_, _) => await DownloadSelectedAsync();
        transferButtons.Children.Add(upload);
        transferButtons.Children.Add(download);
        Grid.SetColumn(transferButtons, 1);
        panes.Children.Add(transferButtons);
        var remote = BuildRemotePane();
        Grid.SetColumn(remote, 2);
        panes.Children.Add(remote);
        Grid.SetRow(panes, 1);
        root.Children.Add(panes);

        var footer = new Border
        {
            Padding = new Thickness(10, 7),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                RowSpacing = 6,
                Children =
                {
                    _status,
                    _progress
                }
            }
        };
        Grid.SetRow(_progress, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        AutomationProperties.SetName(root, "File Transfer");
        return root;
    }

    private Control BuildLocalPane()
    {
        _localList.ItemsSource = _localItems;
        _localList.ItemTemplate = CreateLocalTemplate();
        _localList.DoubleTapped += async (_, _) => await OpenLocalSelectionAsync();
        ConfigureDragSource(_localList, isLocal: true);
        var go = new Button { Content = "Go" };
        go.Click += async (_, _) => await RefreshLocalAsync();
        var up = new Button { Content = "Up" };
        up.Click += async (_, _) =>
        {
            var parent = Directory.GetParent(_localPath.Text ?? string.Empty);
            if (parent is not null) _localPath.Text = parent.FullName;
            await RefreshLocalAsync();
        };
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await RefreshLocalAsync();
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => SetLocalClipboard(false);
        var cut = new Button { Content = "Cut" };
        cut.Click += (_, _) => SetLocalClipboard(true);
        var paste = new Button { Content = "Paste" };
        paste.Click += async (_, _) => await PasteToLocalAsync();
        var delete = new Button { Content = "Delete" };
        delete.Click += async (_, _) => await DeleteLocalAsync();
        var pane = BuildPane("This computer", _localPath, _localList,
            [up, go, refresh, copy, cut, paste, delete]);
        DragDrop.SetAllowDrop(pane, true);
        pane.AddHandler(DragDrop.DropEvent, async (_, args) => await HandleDropToLocalAsync(args));
        return pane;
    }

    private Control BuildRemotePane()
    {
        _remoteList.ItemsSource = _remoteItems;
        _remoteList.ItemTemplate = CreateRemoteTemplate();
        _remoteList.DoubleTapped += async (_, _) => await OpenRemoteSelectionAsync();
        ConfigureDragSource(_remoteList, isLocal: false);
        var go = new Button { Content = "Go" };
        go.Click += async (_, _) => await RefreshRemoteAsync();
        var up = new Button { Content = "Up" };
        up.Click += async (_, _) =>
        {
            _remotePath.Text = ParentRemotePath(_remotePath.Text ?? "/");
            await RefreshRemoteAsync();
        };
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await RefreshRemoteAsync();
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => SetRemoteClipboard(false);
        var cut = new Button { Content = "Cut" };
        cut.Click += (_, _) => SetRemoteClipboard(true);
        var paste = new Button { Content = "Paste" };
        paste.Click += async (_, _) => await PasteToRemoteAsync();
        var folder = new Button { Content = "New folder" };
        folder.Click += async (_, _) => await CreateRemoteFolderAsync();
        var rename = new Button { Content = "Rename" };
        rename.Click += async (_, _) => await RenameRemoteAsync();
        var delete = new Button { Content = "Delete" };
        delete.Click += async (_, _) => await DeleteRemoteAsync();
        var pane = BuildPane("Remote host", _remotePath, _remoteList,
            [up, go, refresh, copy, cut, paste, folder, rename, delete]);
        DragDrop.SetAllowDrop(pane, true);
        pane.AddHandler(DragDrop.DropEvent, async (_, args) => await HandleDropToRemoteAsync(args));
        return pane;
    }

    private static Control BuildPane(
        string title,
        TextBox path,
        ListBox list,
        IReadOnlyList<Button> buttons)
    {
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 8
        };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold });
        var address = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 5 };
        address.Children.Add(path);
        Grid.SetColumn(buttons[0], 1);
        address.Children.Add(buttons[0]);
        Grid.SetColumn(buttons[1], 2);
        address.Children.Add(buttons[1]);
        Grid.SetRow(address, 1);
        panel.Children.Add(address);
        Grid.SetRow(list, 2);
        panel.Children.Add(list);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons.Skip(2))
        {
            button.Margin = new Thickness(0, 0, 6, 5);
            bar.Children.Add(button);
        }
        Grid.SetRow(bar, 3);
        panel.Children.Add(bar);
        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = panel
        };
    }

    private static IDataTemplate CreateLocalTemplate() =>
        new Avalonia.Controls.Templates.FuncDataTemplate<LocalFileItem>((item, _) =>
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 12,
                Margin = new Thickness(7, 5)
            };
            grid.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = (item?.IsDirectory == true ? "📁  " : "📄  ") + item?.Name,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = item is null ? "" : $"{item.Kind} · {item.LastWriteTime:g}",
                        FontSize = 11,
                        Opacity = 0.65,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            });
            var size = new TextBlock
            {
                Text = item?.Size,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(size, 1);
            grid.Children.Add(size);
            return grid;
        });

    private static IDataTemplate CreateRemoteTemplate() =>
        new Avalonia.Controls.Templates.FuncDataTemplate<RemoteFileEntry>((item, _) =>
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(7, 5) };
            grid.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = (item?.IsDirectory == true ? "📁  " : item?.Kind == RemoteFileKind.SymbolicLink ? "🔗  " : "📄  ") + item?.Name },
                    new TextBlock { Text = item is null ? "" : $"{item.Kind} · {item.LastWriteTime:g}", FontSize = 11, Opacity = 0.65 }
                }
            });
            var size = new TextBlock { Text = item is { Kind: RemoteFileKind.File } ? FormatBytes(item.Length) : "", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(size, 1);
            grid.Children.Add(size);
            return grid;
        });

    private async Task InitializeAsync() => await RunAsync(async token =>
    {
        var host = RequireHost();
        if (_viewModel.FileTransferLocations is not null)
        {
            var saved = await _viewModel.FileTransferLocations.LoadAsync(host.Id, token);
            if (!string.IsNullOrWhiteSpace(saved.LocalPath) && Directory.Exists(saved.LocalPath))
            {
                _localPath.Text = saved.LocalPath;
            }
            if (!string.IsNullOrWhiteSpace(saved.RemotePath))
            {
                _remotePath.Text = saved.RemotePath;
            }
        }

        await RefreshLocalCoreAsync(token);
        try
        {
            await RefreshRemoteCoreAsync(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _remotePath.Text = "/";
            await RefreshRemoteCoreAsync(token);
        }
    }, "Folders refreshed.");

    private Task RefreshLocalAsync() => RunAsync(RefreshLocalCoreAsync, "Local folder refreshed.");
    private Task RefreshRemoteAsync() => RunAsync(RefreshRemoteCoreAsync, "Remote folder refreshed.");

    private Task RefreshLocalCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(_localPath.Text ?? string.Empty);
        var items = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(info => new LocalFileItem(
                info.Name, info.FullName, info is DirectoryInfo,
                info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc))
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _localItems.Clear();
        foreach (var item in items) _localItems.Add(item);
        _localPath.Text = path;
        if (_viewModel.FileTransferLocations is not null)
        {
            return _viewModel.FileTransferLocations.SaveLocalPathAsync(path, cancellationToken);
        }
        return Task.CompletedTask;
    }

    private async Task RefreshRemoteCoreAsync(CancellationToken cancellationToken)
    {
        var host = _viewModel.SelectedHost ??
            throw new InvalidOperationException("Select and connect a host before opening Files.");
        var path = _remotePath.Text ?? "/";
        var items = await _viewModel.FileTransfers.ListAsync(host.Id, path, cancellationToken);
        _remoteItems.Clear();
        foreach (var item in items) _remoteItems.Add(item);
        if (_viewModel.FileTransferLocations is not null)
        {
            await _viewModel.FileTransferLocations.SaveRemotePathAsync(
                host.Id,
                path,
                cancellationToken);
        }
    }

    private async Task UploadSelectedAsync()
    {
        var selected = SelectedLocalItems();
        if (selected.Length == 0) return;
        await UploadAsync(selected.Select(item => new LocalTransferSource(item.FullPath, item.IsDirectory)).ToArray());
    }

    private async Task UploadAsync(IReadOnlyList<LocalTransferSource> sources) =>
        await RunTransferAsync(
            (progress, token) => _viewModel.FileTransfers.UploadAsync(
                RequireHost().Id, sources, _remotePath.Text ?? "/", progress, token),
            "Upload complete.", RefreshRemoteCoreAsync);

    private async Task DownloadSelectedAsync()
    {
        var selected = SelectedRemoteItems();
        if (selected.Length == 0) return;
        await DownloadAsync(selected, _localPath.Text ?? string.Empty);
    }

    private async Task DownloadAsync(IReadOnlyList<RemoteFileEntry> entries, string destination) =>
        await RunTransferAsync(
            (progress, token) => _viewModel.FileTransfers.DownloadAsync(
                RequireHost().Id, entries, destination, progress, token),
            "Download complete.", RefreshLocalCoreAsync);

    private async Task DeleteRemoteAsync()
    {
        var selected = SelectedRemoteItems();
        if (selected.Length == 0) return;
        await RunTransferAsync(
            (progress, token) => _viewModel.FileTransfers.DeleteAsync(
                RequireHost().Id, selected, progress, token),
            "Remote items deleted.", RefreshRemoteCoreAsync);
    }

    private async Task DeleteLocalAsync()
    {
        var selected = SelectedLocalItems();
        if (selected.Length == 0 || !await ConfirmLocalDeleteAsync(selected.Length)) return;
        await RunAsync(token => Task.Run(() =>
        {
            foreach (var item in selected)
            {
                token.ThrowIfCancellationRequested();
                if (item.IsDirectory) Directory.Delete(item.FullPath, true); else File.Delete(item.FullPath);
            }
        }, token), "Local items deleted.");
        await RefreshLocalAsync();
    }

    private async Task OpenLocalSelectionAsync()
    {
        if (_localList.SelectedItem is not LocalFileItem item) return;
        if (item.IsDirectory)
        {
            _localPath.Text = item.FullPath;
            await RefreshLocalAsync();
            return;
        }
        OpenWithSystem(item.FullPath);
    }

    private async Task OpenRemoteSelectionAsync()
    {
        if (_remoteList.SelectedItem is not RemoteFileEntry item) return;
        if (item.IsDirectory)
        {
            _remotePath.Text = item.FullPath;
            await RefreshRemoteAsync();
            return;
        }
        if (item.Kind != RemoteFileKind.File) return;
        var root = Path.Combine(Path.GetTempPath(), "Remotune", "opened-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await DownloadAsync([item], root);
        var path = Path.Combine(root, item.Name);
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            _watchedFiles.Add(new(
                path,
                root,
                RequireHost().Id,
                item.FullPath,
                file.LastWriteTimeUtc,
                file.Length));
            _watchTimer.Start();
            OpenWithSystem(path);
        }
    }

    private async Task CreateRemoteFolderAsync()
    {
        var name = await PromptAsync("New remote folder", "Folder name");
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) return;
        await RunAsync(
            token => _viewModel.FileTransfers.CreateDirectoryAsync(
                RequireHost().Id, CombineRemote(_remotePath.Text ?? "/", name), token),
            "Remote folder created.");
        await RefreshRemoteAsync();
    }

    private async Task RenameRemoteAsync()
    {
        if (_remoteList.SelectedItems?.Count != 1 || _remoteList.SelectedItem is not RemoteFileEntry item) return;
        var name = await PromptAsync("Rename remote item", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) return;
        await RunAsync(
            token => _viewModel.FileTransfers.MoveAsync(
                RequireHost().Id, item.FullPath, CombineRemote(_remotePath.Text ?? "/", name), token),
            "Remote item renamed.");
        await RefreshRemoteAsync();
    }

    private void SetLocalClipboard(bool cut)
    {
        var items = SelectedLocalItems();
        if (items.Length == 0) return;
        _clipboard = new LocalClipboard(items, cut);
        _status.Text = $"{(cut ? "Cut" : "Copied")} {items.Length} local item(s).";
    }

    private void SetRemoteClipboard(bool cut)
    {
        var items = SelectedRemoteItems();
        if (items.Length == 0) return;
        _clipboard = new RemoteClipboard(items, cut);
        _status.Text = $"{(cut ? "Cut" : "Copied")} {items.Length} remote item(s).";
    }

    private async Task PasteToRemoteAsync()
    {
        switch (_clipboard)
        {
            case LocalClipboard local:
                await UploadAsync(local.Items.Select(item => new LocalTransferSource(item.FullPath, item.IsDirectory)).ToArray());
                if (local.Cut)
                {
                    foreach (var item in local.Items)
                        if (item.IsDirectory) Directory.Delete(item.FullPath, true); else File.Delete(item.FullPath);
                    _clipboard = null;
                    await RefreshLocalAsync();
                }
                break;
            case RemoteClipboard remote when remote.Cut:
                foreach (var item in remote.Items)
                    await _viewModel.FileTransfers.MoveAsync(
                        RequireHost().Id, item.FullPath,
                        CombineRemote(_remotePath.Text ?? "/", item.Name));
                _clipboard = null;
                await RefreshRemoteAsync();
                break;
            case RemoteClipboard remote:
                var temp = Path.Combine(Path.GetTempPath(), "Remotune", "copy", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                await DownloadAsync(remote.Items, temp);
                await UploadAsync(Directory.EnumerateFileSystemEntries(temp)
                    .Select(path => new LocalTransferSource(path, Directory.Exists(path))).ToArray());
                try { Directory.Delete(temp, true); } catch (IOException) { }
                break;
        }
    }

    private async Task PasteToLocalAsync()
    {
        switch (_clipboard)
        {
            case RemoteClipboard remote:
                await DownloadAsync(remote.Items, _localPath.Text ?? string.Empty);
                if (remote.Cut)
                {
                    await _viewModel.FileTransfers.DeleteAsync(RequireHost().Id, remote.Items);
                    _clipboard = null;
                    await RefreshRemoteAsync();
                }
                break;
            case LocalClipboard local:
                await RunAsync(token => Task.Run(() => CopyOrMoveLocal(local, _localPath.Text ?? string.Empty, token), token),
                    local.Cut ? "Local items moved." : "Local items copied.");
                if (local.Cut) _clipboard = null;
                await RefreshLocalAsync();
                break;
        }
    }

    private void ConfigureDragSource(ListBox list, bool isLocal)
    {
        list.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            _dragStartEvent = args;
            _dragStartPoint = args.GetPosition(list);
        };
        list.PointerReleased += (_, _) => _dragStartEvent = null;
        list.PointerMoved += async (_, args) =>
        {
            if (_dragging || _dragStartEvent is null ||
                !args.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            var position = args.GetPosition(list);
            if (Math.Abs(position.X - _dragStartPoint.X) < 6 &&
                Math.Abs(position.Y - _dragStartPoint.Y) < 6) return;
            if (isLocal) SetLocalClipboard(false); else SetRemoteClipboard(false);
            if (_clipboard is null) return;
            var transfer = new DataTransfer();
            var item = new DataTransferItem();
            item.SetText(isLocal ? "orvian:local-selection" : "orvian:remote-selection");
            transfer.Add(item);
            _dragging = true;
            try
            {
                await DragDrop.DoDragDropAsync(
                    _dragStartEvent, transfer, DragDropEffects.Copy | DragDropEffects.Move);
            }
            finally
            {
                _dragging = false;
                _dragStartEvent = null;
            }
        };
    }

    private async Task HandleDropToRemoteAsync(DragEventArgs args)
    {
        var marker = args.DataTransfer.TryGetText();
        if (marker is "orvian:local-selection" or "orvian:remote-selection")
        {
            await PasteToRemoteAsync();
            return;
        }
        var files = args.DataTransfer.TryGetFiles()?.ToArray();
        if (files is null || files.Length == 0) return;
        var sources = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new LocalTransferSource(path!, Directory.Exists(path)))
            .ToArray();
        if (sources.Length > 0) await UploadAsync(sources);
    }

    private async Task HandleDropToLocalAsync(DragEventArgs args)
    {
        var marker = args.DataTransfer.TryGetText();
        if (marker is "orvian:local-selection" or "orvian:remote-selection")
        {
            await PasteToLocalAsync();
        }
    }

    private async Task<bool> RunTransferAsync(
        Func<IProgress<FileTransferProgress>, CancellationToken, Task<FileTransferBatchResult>> action,
        string success,
        Func<CancellationToken, Task> refresh)
    {
        return await RunAsync(async token =>
        {
            var progress = new Progress<FileTransferProgress>(value =>
            {
                var percent = value.TotalPercent ?? value.ItemPercent;
                _status.Text = percent is null
                    ? $"{value.ItemName}: {FormatBytes(value.ItemBytesTransferred)} · item {value.CompletedItems + 1}/{value.TotalItems}"
                    : $"{value.ItemName}: {percent:0}% · item {value.CompletedItems + 1}/{value.TotalItems}";
                _progress.IsIndeterminate = percent is null;
                if (percent is not null) _progress.Value = percent.Value;
            });
            var result = await action(progress, token);
            if (result.IsPartial)
                throw new InvalidOperationException("The operation partially completed. Review failed items in Activity.");
            await refresh(token);
        }, success);
    }

    private async Task<bool> RunAsync(Func<CancellationToken, Task> action, string success)
    {
        if (_operationCancellation is not null) return false;
        _operationCancellation = new CancellationTokenSource();
        _cancel.IsEnabled = true;
        _progress.Value = 0;
        _progress.IsIndeterminate = true;
        try
        {
            _status.Text = "Working…";
            await action(_operationCancellation.Token);
            _status.Text = success;
            return true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Operation cancelled. A remote partial file may have been removed when possible.";
            return false;
        }
        catch (Exception exception)
        {
            _status.Text = exception is InvalidOperationException or UnauthorizedAccessException
                ? exception.Message
                : "The file operation failed. Review Activity for safe details.";
            return false;
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _cancel.IsEnabled = false;
            _progress.IsIndeterminate = false;
            if (_progress.Value < 100 && _status.Text == success) _progress.Value = 100;
        }
    }

    private async Task CheckWatchedFilesAsync()
    {
        if (_watchCheckRunning || _operationCancellation is not null)
        {
            return;
        }

        _watchCheckRunning = true;
        try
        {
            foreach (var watched in _watchedFiles.ToArray())
            {
                if (!File.Exists(watched.LocalPath))
                {
                    _watchedFiles.Remove(watched);
                    continue;
                }

                var info = new FileInfo(watched.LocalPath);
                if (info.LastWriteTimeUtc == watched.LastWriteTimeUtc &&
                    info.Length == watched.Length)
                {
                    continue;
                }

                var uploaded = await RunTransferAsync(
                    (progress, token) => _viewModel.FileTransfers.UploadAsync(
                        watched.HostId,
                        [new(watched.LocalPath, IsDirectory: false)],
                        ParentRemotePath(watched.RemotePath),
                        progress,
                        token),
                    $"Uploaded changed file {Path.GetFileName(watched.LocalPath)}.",
                    _ => Task.CompletedTask);
                if (uploaded || _operationCancellation is null)
                {
                    info.Refresh();
                    watched.LastWriteTimeUtc = info.LastWriteTimeUtc;
                    watched.Length = info.Length;
                }
            }
        }
        finally
        {
            _watchCheckRunning = false;
            if (_watchedFiles.Count == 0)
            {
                _watchTimer.Stop();
            }
        }
    }

    internal async Task<bool> ConfirmCleanupAsync()
    {
        if (_watchedFiles.Count == 0)
        {
            return true;
        }

        _watchTimer.Stop();
        var decision = await ShowCleanupDialogAsync(_watchedFiles.Count);
        if (decision == CleanupDecision.Cancel)
        {
            _watchTimer.Start();
            return false;
        }

        if (decision == CleanupDecision.Delete)
        {
            foreach (var directory in _watchedFiles
                         .Select(file => file.WorkingDirectory)
                         .Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    _status.Text = "Some downloaded working files are still in use and could not be deleted.";
                }
            }
        }

        _watchedFiles.Clear();
        return true;
    }

    private async Task<CleanupDecision> ShowCleanupDialogAsync(int fileCount)
    {
        var dialog = new Window
        {
            Title = "Downloaded working files",
            Width = 500,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var delete = new Button { Content = "Delete downloads" };
        var keep = new Button { Content = "Keep files" };
        var cancel = new Button { Content = "Cancel" };
        delete.Click += (_, _) => dialog.Close(CleanupDecision.Delete);
        keep.Click += (_, _) => dialog.Close(CleanupDecision.Keep);
        cancel.Click += (_, _) => dialog.Close(CleanupDecision.Cancel);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = $"{fileCount} remote working file(s) were downloaded and watched for changes. Delete them before leaving File Transfer?",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Keeping files stops change monitoring and leaves the downloaded copies on this computer.",
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, keep, delete }
                }
            }
        };
        return await dialog.ShowDialog<CleanupDecision>(_owner);
    }

    private void CancelOperation() => _operationCancellation?.Cancel();
    private HostListItemViewModel RequireHost() => _viewModel.SelectedHost ??
        throw new InvalidOperationException("Select a host first.");

    private async Task<string?> PromptAsync(string title, string placeholder)
    {
        var input = new TextBox { PlaceholderText = placeholder, MinWidth = 320 };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var ok = new Button { Content = "Continue" };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) => dialog.Close(input.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children = { input, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } } }
        };
        return await dialog.ShowDialog<string?>(_owner);
    }

    private async Task<bool> ConfirmLocalDeleteAsync(int count)
    {
        var dialog = new Window
        {
            Title = "Delete local items?", Width = 440, Height = 180, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var delete = new Button { Content = "Delete permanently" };
        var cancel = new Button { Content = "Cancel" };
        delete.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"Permanently delete {count} selected item(s) from this computer?", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, delete } }
            }
        };
        return await dialog.ShowDialog<bool>(_owner);
    }

    private static void OpenWithSystem(string path) => Process.Start(new ProcessStartInfo
    {
        FileName = path,
        UseShellExecute = true
    });

    private static void CopyOrMoveLocal(LocalClipboard clipboard, string targetDirectory, CancellationToken token)
    {
        foreach (var item in clipboard.Items)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(targetDirectory, item.Name);
            if (clipboard.Cut)
            {
                if (item.IsDirectory) Directory.Move(item.FullPath, target); else File.Move(item.FullPath, target, true);
            }
            else if (item.IsDirectory)
            {
                CopyDirectory(item.FullPath, target, token);
            }
            else File.Copy(item.FullPath, target, true);
        }
    }

    private static void CopyDirectory(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            token.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), token);
        }
    }

    private static string CombineRemote(string parent, string name) =>
        parent == "/" ? "/" + name : parent.TrimEnd('/') + "/" + name;
    private static string ParentRemotePath(string path)
    {
        var normalized = path.TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" :
        bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" :
        $"{bytes / (1024d * 1024 * 1024):0.#} GB";

    private LocalFileItem[] SelectedLocalItems() =>
        _localList.SelectedItems?.OfType<LocalFileItem>().ToArray() ?? [];

    private RemoteFileEntry[] SelectedRemoteItems() =>
        _remoteList.SelectedItems?.OfType<RemoteFileEntry>().ToArray() ?? [];

    private abstract record ClipboardPayload(bool Cut);
    private sealed record LocalClipboard(IReadOnlyList<LocalFileItem> Items, bool Cut) : ClipboardPayload(Cut);
    private sealed record RemoteClipboard(IReadOnlyList<RemoteFileEntry> Items, bool Cut) : ClipboardPayload(Cut);

    private sealed class WatchedRemoteFile(
        string localPath,
        string workingDirectory,
        Orvian.Core.Hosts.HostProfileId hostId,
        string remotePath,
        DateTime lastWriteTimeUtc,
        long length)
    {
        public string LocalPath { get; } = localPath;
        public string WorkingDirectory { get; } = workingDirectory;
        public Orvian.Core.Hosts.HostProfileId HostId { get; } = hostId;
        public string RemotePath { get; } = remotePath;
        public DateTime LastWriteTimeUtc { get; set; } = lastWriteTimeUtc;
        public long Length { get; set; } = length;
    }

    private enum CleanupDecision
    {
        Cancel,
        Keep,
        Delete
    }
}
