#nullable enable

using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

internal sealed partial class FileBrowserBody : UserControl, IDisposable
{
	private enum LocationKind
	{
		ThisPc,
		Folder,
		Network
	}

	private sealed class ExplorerLocation
	{
		public LocationKind Kind { get; init; }

		public string? Path { get; init; }

		public static ExplorerLocation ThisPc() => new ExplorerLocation { Kind = LocationKind.ThisPc };

		public static ExplorerLocation Network() => new ExplorerLocation { Kind = LocationKind.Network };

		public static ExplorerLocation Folder(string path) => new ExplorerLocation
		{
			Kind = LocationKind.Folder,
			Path = path
		};
	}

	private sealed class KnownFolderDefinition
	{
		public string Name { get; init; } = "";

		public string Path { get; init; } = "";

		public string Asset { get; init; } = "folder";
	}

	private sealed class DriveSnapshot
	{
		public string Path { get; init; } = "";

		public string Display { get; init; } = "";

		public string Secondary { get; init; } = "";

		public string Asset { get; init; } = "drive-fixed81.ico";

		public double UsedPercent { get; init; }
	}

	private static readonly KnownFolderDefinition[] KnownFolders =
	{
		new KnownFolderDefinition { Name = "Desktop", Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Asset = "desktop" },
		new KnownFolderDefinition { Name = "Documents", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Asset = "documents" },
		new KnownFolderDefinition { Name = "Downloads", Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Asset = "downloads" },
		new KnownFolderDefinition { Name = "Music", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Asset = "music" },
		new KnownFolderDefinition { Name = "Pictures", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Asset = "pictures" },
		new KnownFolderDefinition { Name = "Videos", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Asset = "videos" }
	};

	private static readonly ConcurrentDictionary<string, ImageSource> AssetCache = new(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentDictionary<string, WeakReference<ImageSource>> ShellIconCache = new(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentDictionary<string, string> TypeNameCache = new(StringComparer.OrdinalIgnoreCase);

	private static readonly SemaphoreSlim IconGate = new(6, 6);

	private static Style? _explorerMenuItemStyle;

	private static Style? _explorerMenuSeparatorStyle;

	private readonly DispatcherTimer _refreshTimer;

	private readonly DispatcherTimer _searchTimer;

	private readonly List<ExplorerLocation> _history = new();

	private readonly TaskCompletionSource<bool> _firstReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private CancellationTokenSource? _navigationCts;

	private CancellationTokenSource? _searchCts;

	private FileSystemWatcher? _watcher;

	private ExplorerLocation _current = ExplorerLocation.ThisPc();

	private List<FileItem> _allItems = new();

	private List<FileItem> _thisPcFolders = new();

	private List<FileItem> _thisPcDrives = new();

	private TreeViewItem? _thisPcNode;

	private TreeViewItem? _networkNode;

	private Border? _thisPcSelection;

	private FileItem? _selectedThisPcItem;

	private string? _pendingPath;

	private string? _pendingSelectPath;

	private string _viewMode;

	private int _historyIndex = -1;

	private int _navigationGeneration;

	private int _searchGeneration;

	private bool _started;

	private bool _suppressTreeNavigation;

	private bool _ribbonExpanded;

	private bool _disposed;

	private long _lastNavigationMs;

	internal event Action<string>? TitleChanged;

	internal int QaFolderCount => _thisPcFolders.Count;

	internal int QaDriveCount => _thisPcDrives.Count;

	internal long QaLastNavigationMilliseconds => Interlocked.Read(ref _lastNavigationMs);

	internal string QaStatusText => StatusText.Text;

	internal int QaVisibleItemCount => _current.Kind == LocationKind.ThisPc
		? _thisPcFolders.Count + _thisPcDrives.Count
		: FileList.Items.Count;

	internal string QaCurrentTitle => LocationTitle(_current);

	internal string? QaFirstItemDisplay => _allItems.FirstOrDefault()?.Display;

	internal string QaViewMode => _viewMode;

	internal FileBrowserBody()
	{
		InitializeComponent();
		_viewMode = NormalizeViewMode(SettingsStore.Current.Explorer81ViewMode);
		_refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(220)
		};
		_refreshTimer.Tick += RefreshTimer_Tick;
		_searchTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(120)
		};
		_searchTimer.Tick += SearchTimer_Tick;
		BuildNavigationTree();
		SetNavigationPaneVisible(SettingsStore.Current.Explorer81NavigationPane, persist: false);
		ApplyViewMode(_viewMode, persist: false);
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		FileList.SelectionChanged += FileList_SelectionChanged;
		FileList.SizeChanged += FileList_SizeChanged;
		ContentHost.PreviewMouseRightButtonUp += ContentHost_PreviewMouseRightButtonUp;
	}

	internal void Open(string? path = null, string? selectPath = null)
	{
		if (_disposed)
		{
			return;
		}
		_pendingPath = path;
		_pendingSelectPath = selectPath;
		if (IsLoaded)
		{
			_ = NavigateRequestedAsync(path, selectPath, pushHistory: true);
			_pendingPath = null;
			_pendingSelectPath = null;
		}
	}

	internal async Task WaitForReadyAsync(TimeSpan timeout)
	{
		await _firstReady.Task.WaitAsync(timeout);
	}

	internal Task QaNavigateAsync(string path) => NavigateRequestedAsync(path, null, pushHistory: true);

	internal async Task QaBackAsync()
	{
		if (_historyIndex <= 0)
		{
			return;
		}
		_historyIndex--;
		await NavigateAsync(_history[_historyIndex], null, pushHistory: false);
	}

	internal async Task QaSearchAsync(string query)
	{
		_searchTimer.Stop();
		SearchBox.Text = query ?? "";
		_searchTimer.Stop();
		await ApplySearchAsync(query);
	}

	internal void QaSetViewMode(string mode) => ApplyViewMode(mode, persist: false);

	internal bool QaMeasureMenus()
	{
		ContextMenu[] menus =
		{
			BuildBackgroundMenu(),
			BuildFileMenu()
		};
		foreach (ContextMenu menu in menus)
		{
			menu.ApplyTemplate();
			menu.Measure(new Size(420, 900));
			menu.Arrange(new Rect(menu.DesiredSize));
			menu.UpdateLayout();
		}
		return true;
	}

	internal void RenderForQa(string outputPath, double width, double height)
	{
		if (string.IsNullOrWhiteSpace(outputPath))
		{
			throw new ArgumentException("QA output path is required.", nameof(outputPath));
		}
		Measure(new Size(width, height));
		Arrange(new Rect(0, 0, width, height));
		UpdateLayout();
		int pixelWidth = Math.Max(1, (int)Math.Ceiling(width));
		int pixelHeight = Math.Max(1, (int)Math.Ceiling(height));
		RenderTargetBitmap bitmap = new(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(this);
		PngBitmapEncoder encoder = new();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
		using FileStream stream = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
		encoder.Save(stream);
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (_started || _disposed)
		{
			return;
		}
		_started = true;
		string? path = _pendingPath;
		string? select = _pendingSelectPath;
		_pendingPath = null;
		_pendingSelectPath = null;
		await NavigateRequestedAsync(path, select, pushHistory: true);
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		Dispose();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_refreshTimer.Stop();
		_searchTimer.Stop();
		_navigationCts?.Cancel();
		_searchCts?.Cancel();
		_navigationCts?.Dispose();
		_searchCts?.Dispose();
		_navigationCts = null;
		_searchCts = null;
		DisposeWatcher();
	}

	private async Task NavigateRequestedAsync(string? path, string? selectPath, bool pushHistory)
	{
		ExplorerLocation requested = ResolveLocation(path, ref selectPath);
		await NavigateAsync(requested, selectPath, pushHistory);
	}

	private static ExplorerLocation ResolveLocation(string? path, ref string? selectPath)
	{
		if (string.IsNullOrWhiteSpace(path)
			|| path.Equals("This PC", StringComparison.OrdinalIgnoreCase)
			|| path.Equals("shell:MyComputerFolder", StringComparison.OrdinalIgnoreCase))
		{
			return ExplorerLocation.ThisPc();
		}
		string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
		if (expanded.Equals("Network", StringComparison.OrdinalIgnoreCase)
			|| expanded.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase))
		{
			return ExplorerLocation.Network();
		}
		try
		{
			if (File.Exists(expanded))
			{
				selectPath ??= expanded;
				string? parent = System.IO.Path.GetDirectoryName(expanded);
				return !string.IsNullOrWhiteSpace(parent) ? ExplorerLocation.Folder(parent) : ExplorerLocation.ThisPc();
			}
			return ExplorerLocation.Folder(System.IO.Path.GetFullPath(expanded));
		}
		catch
		{
			return ExplorerLocation.Folder(expanded);
		}
	}

	private async Task NavigateAsync(ExplorerLocation requested, string? selectPath, bool pushHistory)
	{
		if (_disposed)
		{
			return;
		}
		int generation = Interlocked.Increment(ref _navigationGeneration);
		CancellationTokenSource next = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _navigationCts, next);
		previous?.Cancel();
		previous?.Dispose();
		CancellationToken token = next.Token;
		Stopwatch sw = Stopwatch.StartNew();
		LoadingOverlay.Visibility = requested.Kind == LocationKind.Folder ? Visibility.Visible : Visibility.Collapsed;
		try
		{
			if (requested.Kind == LocationKind.Folder)
			{
				bool exists = await Task.Run(() => !string.IsNullOrWhiteSpace(requested.Path) && Directory.Exists(requested.Path), token);
				if (!exists)
				{
					ShowNavigationError("The location is not available.");
					return;
				}
			}
			if (pushHistory)
			{
				PushHistory(requested);
			}
			_current = requested;
			UpdateWatcher();
			UpdateLocationChrome();
			if (requested.Kind == LocationKind.ThisPc)
			{
				await PopulateThisPcAsync(generation, token);
			}
			else if (requested.Kind == LocationKind.Network)
			{
				await PopulateNetworkAsync(generation, token);
			}
			else
			{
				List<FileItem> items = await Task.Run(() => BuildDirectoryItems(requested.Path!, token), token);
				token.ThrowIfCancellationRequested();
				if (generation != _navigationGeneration)
				{
					return;
				}
				foreach (FileItem item in items)
				{
					item.Icon = Asset(item.IsDir ? "folder-16.png" : "file-16.png");
				}
				_allItems = items;
				ShowDirectoryItems(items, selectPath);
			}
			UpdateNavigationButtons();
			UpdateTreeSelection();
			AnimateContent();
			_firstReady.TrySetResult(true);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Logger.Log("Explorer81 navigate: " + ex);
			ShowNavigationError("This location could not be opened.");
			_firstReady.TrySetResult(true);
		}
		finally
		{
			if (generation == _navigationGeneration)
			{
				LoadingOverlay.Visibility = Visibility.Collapsed;
				sw.Stop();
				Interlocked.Exchange(ref _lastNavigationMs, sw.ElapsedMilliseconds);
			}
		}
	}

	private void PushHistory(ExplorerLocation requested)
	{
		if (_historyIndex >= 0 && SameLocation(_history[_historyIndex], requested))
		{
			return;
		}
		if (_historyIndex < _history.Count - 1)
		{
			_history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
		}
		_history.Add(requested);
		_historyIndex = _history.Count - 1;
	}

	private static bool SameLocation(ExplorerLocation left, ExplorerLocation right)
	{
		return left.Kind == right.Kind && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
	}

	private async Task PopulateThisPcAsync(int generation, CancellationToken token)
	{
		List<FileItem> folders = new();
		foreach (KnownFolderDefinition known in KnownFolders)
		{
			if (!string.IsNullOrWhiteSpace(known.Path) && Directory.Exists(known.Path))
			{
				folders.Add(new FileItem
				{
					Path = known.Path,
					Display = known.Name,
					IsDir = true,
					TypeText = "File folder",
					AssetKey = known.Asset,
					Icon = Asset(known.Asset + "-48.png")
				});
			}
		}
		List<DriveSnapshot> snapshots = await Task.Run(() => ReadDrives(networkOnly: false, token), token);
		token.ThrowIfCancellationRequested();
		if (generation != _navigationGeneration)
		{
			return;
		}
		List<FileItem> drives = snapshots.Select(snapshot => new FileItem
		{
			Path = snapshot.Path,
			Display = snapshot.Display,
			IsDir = true,
			IsDrive = true,
			TypeText = "Drive",
			SecondaryText = snapshot.Secondary,
			UsagePercent = snapshot.UsedPercent,
			HasUsageBar = true,
			AssetKey = snapshot.Asset,
			Icon = Asset(snapshot.Asset)
		}).ToList();
		_thisPcFolders = folders;
		_thisPcDrives = drives;
		_allItems = folders.Concat(drives).ToList();
		ShowThisPcItems(folders, drives);
	}

	private async Task PopulateNetworkAsync(int generation, CancellationToken token)
	{
		List<DriveSnapshot> snapshots = await Task.Run(() => ReadDrives(networkOnly: true, token), token);
		token.ThrowIfCancellationRequested();
		if (generation != _navigationGeneration)
		{
			return;
		}
		List<FileItem> items = snapshots.Select(snapshot => new FileItem
		{
			Path = snapshot.Path,
			Display = snapshot.Display,
			IsDir = true,
			IsDrive = true,
			TypeText = "Network drive",
			SecondaryText = snapshot.Secondary,
			UsagePercent = snapshot.UsedPercent,
			HasUsageBar = true,
			Icon = Asset("network81.ico")
		}).ToList();
		_allItems = items;
		ShowDirectoryItems(items, null);
		if (items.Count == 0)
		{
			EmptyStateText.Text = "No network locations are available.";
		}
	}

	private static List<DriveSnapshot> ReadDrives(bool networkOnly, CancellationToken token)
	{
		List<DriveSnapshot> result = new();
		IEnumerable<DriveInfo> drives;
		try
		{
			drives = DriveInfo.GetDrives();
		}
		catch
		{
			return result;
		}
		string systemRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
		foreach (DriveInfo drive in drives)
		{
			token.ThrowIfCancellationRequested();
			try
			{
				if (networkOnly != (drive.DriveType == DriveType.Network))
				{
					continue;
				}
				string root = drive.Name;
				string defaultLabel = drive.DriveType switch
				{
					DriveType.CDRom => "CD Drive",
					DriveType.Removable => "Removable Disk",
					DriveType.Network => "Network Drive",
					_ => "Local Disk"
				};
				string label = defaultLabel;
				string secondary = "Not ready";
				double used = 0;
				if (drive.IsReady)
				{
					if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
					{
						label = drive.VolumeLabel;
					}
					long total = Math.Max(0, drive.TotalSize);
					long free = Math.Max(0, drive.AvailableFreeSpace);
					used = total <= 0 ? 0 : Math.Clamp((total - free) * 100.0 / total, 0, 100);
					secondary = FormatBytes(free) + " free of " + FormatBytes(total);
				}
				string asset = drive.DriveType == DriveType.CDRom
					? "drive-optical81.ico"
					: drive.DriveType == DriveType.Removable
						? "drive-usb81.ico"
						: string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase)
							? "drive-system81.ico"
							: "drive-fixed81.ico";
				result.Add(new DriveSnapshot
				{
					Path = root,
					Display = label + " (" + root.TrimEnd('\\') + ")",
					Secondary = secondary,
					Asset = asset,
					UsedPercent = used
				});
			}
			catch
			{
			}
		}
		return result.OrderBy(drive => drive.Path, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static List<FileItem> BuildDirectoryItems(string path, CancellationToken token)
	{
		List<FileItem> result = new();
		EnumerationOptions options = new()
		{
			IgnoreInaccessible = true,
			RecurseSubdirectories = false,
			ReturnSpecialDirectories = false,
			AttributesToSkip = 0
		};
		foreach (string entryPath in Directory.EnumerateFileSystemEntries(path, "*", options))
		{
			token.ThrowIfCancellationRequested();
			try
			{
				FileAttributes attributes = File.GetAttributes(entryPath);
				bool isDirectory = (attributes & FileAttributes.Directory) != 0;
				DateTime modified;
				long size = 0;
				if (isDirectory)
				{
					DirectoryInfo directory = new(entryPath);
					modified = directory.LastWriteTime;
				}
				else
				{
					FileInfo file = new(entryPath);
					modified = file.LastWriteTime;
					size = file.Length;
				}
				result.Add(new FileItem
				{
					Path = entryPath,
					Display = System.IO.Path.GetFileName(entryPath),
					IsDir = isDirectory,
					DateModifiedText = modified.ToString("g", CultureInfo.CurrentCulture),
					TypeText = isDirectory ? "File folder" : FriendlyTypeName(entryPath),
					SizeText = isDirectory ? "" : FormatBytes(size),
					AssetKey = isDirectory ? "folder" : "file"
				});
			}
			catch
			{
			}
		}
		return result.OrderByDescending(item => item.IsDir).ThenBy(item => item.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	private static string FriendlyTypeName(string path)
	{
		string extension = System.IO.Path.GetExtension(path);
		if (string.IsNullOrWhiteSpace(extension))
		{
			return "File";
		}
		return TypeNameCache.GetOrAdd(extension, ext =>
		{
			try
			{
				using RegistryKey? extensionKey = Registry.ClassesRoot.OpenSubKey(ext);
				string? className = extensionKey?.GetValue(null) as string;
				if (!string.IsNullOrWhiteSpace(className))
				{
					using RegistryKey? classKey = Registry.ClassesRoot.OpenSubKey(className);
					string? friendly = classKey?.GetValue(null) as string;
					if (!string.IsNullOrWhiteSpace(friendly))
					{
						return friendly;
					}
				}
			}
			catch
			{
			}
			return ext.TrimStart('.').ToUpperInvariant() + " File";
		});
	}

	private void ShowThisPcItems(List<FileItem> folders, List<FileItem> drives)
	{
		ThisPcView.Visibility = Visibility.Visible;
		FileList.Visibility = Visibility.Collapsed;
		EmptyState.Visibility = Visibility.Collapsed;
		FolderItems.ItemsSource = folders;
		DriveItems.ItemsSource = drives;
		DrivesHeading.Text = $"\u25BE  Devices and drives ({drives.Count})";
		ClearThisPcSelection();
		UpdateStatus(folders.Count + drives.Count);
	}

	private void ShowDirectoryItems(IReadOnlyList<FileItem> items, string? selectPath)
	{
		ThisPcView.Visibility = Visibility.Collapsed;
		FileList.Visibility = Visibility.Visible;
		FileList.ItemsSource = items;
		EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		EmptyStateText.Text = "This folder is empty.";
		UpdateStatus(items.Count);
		if (!string.IsNullOrWhiteSpace(selectPath))
		{
			FileItem? selected = items.FirstOrDefault(item => string.Equals(item.Path, selectPath, StringComparison.OrdinalIgnoreCase));
			if (selected != null)
			{
				FileList.SelectedItem = selected;
				FileList.ScrollIntoView(selected);
			}
		}
	}

	private void ShowNavigationError(string message)
	{
		ThisPcView.Visibility = Visibility.Collapsed;
		FileList.Visibility = Visibility.Collapsed;
		EmptyStateText.Text = message;
		EmptyState.Visibility = Visibility.Visible;
		StatusText.Text = "0 items";
	}

	private void UpdateLocationChrome()
	{
		string title = LocationTitle(_current);
		TitleChanged?.Invoke(title);
		SearchPlaceholder.Text = "Search " + title;
		SearchBox.Text = "";
		BuildBreadcrumb();
	}

	private static string LocationTitle(ExplorerLocation location)
	{
		if (location.Kind == LocationKind.ThisPc)
		{
			return "This PC";
		}
		if (location.Kind == LocationKind.Network)
		{
			return "Network";
		}
		try
		{
			string path = location.Path ?? "";
			string root = System.IO.Path.GetPathRoot(path) ?? "";
			if (string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
			{
				return path.TrimEnd('\\');
			}
			return new DirectoryInfo(path).Name;
		}
		catch
		{
			return location.Path ?? "Files";
		}
	}

	private void BuildBreadcrumb()
	{
		BreadcrumbHost.Children.Clear();
		if (_current.Kind == LocationKind.Network)
		{
			AddBreadcrumb("Network", ExplorerLocation.Network(), "network81.ico");
			AddressBox.Text = "Network";
			return;
		}
		AddBreadcrumb("This PC", ExplorerLocation.ThisPc(), "thispc.ico");
		if (_current.Kind == LocationKind.ThisPc || string.IsNullOrWhiteSpace(_current.Path))
		{
			AddressBox.Text = "This PC";
			return;
		}
		string fullPath = _current.Path;
		AddressBox.Text = fullPath;
		try
		{
			string root = System.IO.Path.GetPathRoot(fullPath) ?? fullPath;
			AddBreadcrumbSeparator();
			AddBreadcrumb(root.TrimEnd('\\'), ExplorerLocation.Folder(root), null);
			string remaining = fullPath.Substring(Math.Min(root.Length, fullPath.Length)).Trim('\\');
			string accumulated = root;
			foreach (string segment in remaining.Split('\\', StringSplitOptions.RemoveEmptyEntries))
			{
				accumulated = System.IO.Path.Combine(accumulated, segment);
				AddBreadcrumbSeparator();
				AddBreadcrumb(segment, ExplorerLocation.Folder(accumulated), null);
			}
		}
		catch
		{
		}
	}

	private void AddBreadcrumb(string label, ExplorerLocation target, string? asset)
	{
		StackPanel content = new() { Orientation = Orientation.Horizontal };
		if (!string.IsNullOrWhiteSpace(asset))
		{
			content.Children.Add(new Image
			{
				Source = Asset(asset), Width = 16, Height = 16, Margin = new Thickness(0, 0, 5, 0), Stretch = Stretch.Uniform
			});
		}
		content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
		Button button = new() { Content = content, Tag = target, Style = (Style)Resources["ExplorerBreadcrumbButton"] };
		button.Click += Breadcrumb_Click;
		BreadcrumbHost.Children.Add(button);
	}

	private void AddBreadcrumbSeparator()
	{
		BreadcrumbHost.Children.Add(new TextBlock
		{
			Text = ">", Foreground = Brushes.Gray, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center
		});
	}

	private async void Breadcrumb_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: ExplorerLocation location })
		{
			await NavigateAsync(location, null, pushHistory: true);
		}
	}

	private void BuildNavigationTree()
	{
		NavigationTree.Items.Clear();
		TreeViewItem favourites = NavNode("Favourites", null, "favorites-16.png");
		favourites.IsExpanded = true;
		AddKnownChild(favourites, "Desktop");
		AddKnownChild(favourites, "Downloads");
		string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
		if (!string.IsNullOrWhiteSpace(recent) && Directory.Exists(recent))
		{
			favourites.Items.Add(NavNode("Recent places", ExplorerLocation.Folder(recent), "folder-16.png"));
		}
		NavigationTree.Items.Add(favourites);
		_thisPcNode = NavNode("This PC", ExplorerLocation.ThisPc(), "thispc.ico");
		_thisPcNode.IsExpanded = false;
		foreach (KnownFolderDefinition known in KnownFolders)
		{
			if (!string.IsNullOrWhiteSpace(known.Path) && Directory.Exists(known.Path))
			{
				_thisPcNode.Items.Add(NavNode(known.Name, ExplorerLocation.Folder(known.Path), known.Asset + "-16.png"));
			}
		}
		NavigationTree.Items.Add(_thisPcNode);
		_networkNode = NavNode("Network", ExplorerLocation.Network(), "network81.ico");
		NavigationTree.Items.Add(_networkNode);
		_suppressTreeNavigation = true;
		_thisPcNode.IsSelected = true;
		_suppressTreeNavigation = false;
	}

	private void AddKnownChild(TreeViewItem parent, string name)
	{
		KnownFolderDefinition? known = KnownFolders.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		if (known != null && !string.IsNullOrWhiteSpace(known.Path) && Directory.Exists(known.Path))
		{
			parent.Items.Add(NavNode(known.Name, ExplorerLocation.Folder(known.Path), known.Asset + "-16.png"));
		}
	}

	private TreeViewItem NavNode(string label, ExplorerLocation? target, string asset)
	{
		StackPanel header = new() { Orientation = Orientation.Horizontal };
		header.Children.Add(new Image
		{
			Source = Asset(asset), Width = 16, Height = 16, Margin = new Thickness(0, 0, 5, 0), Stretch = Stretch.Uniform, SnapsToDevicePixels = true
		});
		header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
		return new TreeViewItem { Header = header, Tag = target, ToolTip = label };
	}

	private async void NavigationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
	{
		if (!_suppressTreeNavigation && e.NewValue is TreeViewItem { Tag: ExplorerLocation location })
		{
			await NavigateAsync(location, null, pushHistory: true);
		}
	}

	private void UpdateTreeSelection()
	{
		TreeViewItem? target = _current.Kind switch
		{
			LocationKind.ThisPc => _thisPcNode,
			LocationKind.Network => _networkNode,
			_ => FindTreeNodeByPath(NavigationTree.Items, _current.Path)
		};
		if (target == null || target.IsSelected)
		{
			return;
		}
		_suppressTreeNavigation = true;
		target.IsSelected = true;
		target.BringIntoView();
		_suppressTreeNavigation = false;
	}

	private static TreeViewItem? FindTreeNodeByPath(ItemCollection items, string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		foreach (object value in items)
		{
			if (value is not TreeViewItem node)
			{
				continue;
			}
			if (node.Tag is ExplorerLocation location && location.Kind == LocationKind.Folder && string.Equals(location.Path, path, StringComparison.OrdinalIgnoreCase))
			{
				return node;
			}
			TreeViewItem? child = FindTreeNodeByPath(node.Items, path);
			if (child != null)
			{
				return child;
			}
		}
		return null;
	}

	private void UpdateNavigationButtons()
	{
		BackButton.IsEnabled = _historyIndex > 0;
		ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
		UpButton.IsEnabled = _current.Kind == LocationKind.Folder;
	}

	private async void BackButton_Click(object sender, RoutedEventArgs e)
	{
		if (_historyIndex <= 0) return;
		_historyIndex--;
		await NavigateAsync(_history[_historyIndex], null, pushHistory: false);
	}

	private async void ForwardButton_Click(object sender, RoutedEventArgs e)
	{
		if (_historyIndex < 0 || _historyIndex >= _history.Count - 1) return;
		_historyIndex++;
		await NavigateAsync(_history[_historyIndex], null, pushHistory: false);
	}

	private async void UpButton_Click(object sender, RoutedEventArgs e) => await GoUpAsync();

	private async Task GoUpAsync()
	{
		if (_current.Kind != LocationKind.Folder || string.IsNullOrWhiteSpace(_current.Path)) return;
		DirectoryInfo? parent = null;
		try { parent = Directory.GetParent(_current.Path); } catch { }
		await NavigateAsync(parent == null ? ExplorerLocation.ThisPc() : ExplorerLocation.Folder(parent.FullName), null, pushHistory: true);
	}

	private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

	private async void RibbonRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

	private async Task RefreshAsync() => await NavigateAsync(_current, null, pushHistory: false);

	private void UpdateWatcher()
	{
		DisposeWatcher();
		_refreshTimer.Stop();
		if (_current.Kind != LocationKind.Folder || string.IsNullOrWhiteSpace(_current.Path)) return;
		try
		{
			_watcher = new FileSystemWatcher(_current.Path)
			{
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
				IncludeSubdirectories = false,
				EnableRaisingEvents = true
			};
			_watcher.Created += WatcherChanged;
			_watcher.Deleted += WatcherChanged;
			_watcher.Changed += WatcherChanged;
			_watcher.Renamed += WatcherRenamed;
		}
		catch (Exception ex)
		{
			Logger.Log("Explorer81 watcher " + _current.Path + ": " + ex.Message);
			DisposeWatcher();
		}
	}

	private void WatcherChanged(object sender, FileSystemEventArgs e) => QueueWatcherRefresh();

	private void WatcherRenamed(object sender, RenamedEventArgs e) => QueueWatcherRefresh();

	private void QueueWatcherRefresh()
	{
		try
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
			{
				if (_disposed) return;
				_refreshTimer.Stop();
				_refreshTimer.Start();
			}));
		}
		catch { }
	}

	private async void RefreshTimer_Tick(object? sender, EventArgs e)
	{
		_refreshTimer.Stop();
		await RefreshAsync();
	}

	private void DisposeWatcher()
	{
		FileSystemWatcher? watcher = Interlocked.Exchange(ref _watcher, null);
		if (watcher == null) return;
		try
		{
			watcher.EnableRaisingEvents = false;
			watcher.Created -= WatcherChanged;
			watcher.Deleted -= WatcherChanged;
			watcher.Changed -= WatcherChanged;
			watcher.Renamed -= WatcherRenamed;
			watcher.Dispose();
		}
		catch { }
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
		_searchTimer.Stop();
		_searchTimer.Start();
	}

	private void SearchBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			SearchBox.Text = "";
			Keyboard.ClearFocus();
			e.Handled = true;
		}
	}

	private async void SearchTimer_Tick(object? sender, EventArgs e)
	{
		_searchTimer.Stop();
		await ApplySearchAsync(SearchBox.Text);
	}

	private async Task ApplySearchAsync(string? rawQuery)
	{
		string query = (rawQuery ?? "").Trim();
		int generation = Interlocked.Increment(ref _searchGeneration);
		CancellationTokenSource next = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _searchCts, next);
		previous?.Cancel();
		previous?.Dispose();
		if (query.Length == 0)
		{
			if (_current.Kind == LocationKind.ThisPc) ShowThisPcItems(_thisPcFolders, _thisPcDrives);
			else ShowDirectoryItems(_allItems, null);
			return;
		}
		try
		{
			List<FileItem> filtered = await Task.Run(() => _allItems.Where(item => item.Display.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.TypeText.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList(), next.Token);
			if (generation != _searchGeneration || next.IsCancellationRequested) return;
			if (_current.Kind == LocationKind.ThisPc)
			{
				ShowThisPcItems(filtered.Where(item => !item.IsDrive).ToList(), filtered.Where(item => item.IsDrive).ToList());
			}
			else ShowDirectoryItems(filtered, null);
		}
		catch (OperationCanceledException) { }
	}

	private void FileRow_Loaded(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: FileItem item }) RequestShellIcon(item, _viewMode == "Icons" ? 48 : 20);
	}

	private async void RequestShellIcon(FileItem item, int pixels)
	{
		if (_disposed || item.IsDir || item.IconRequested) return;
		item.IconRequested = true;
		string cacheKey = pixels.ToString(CultureInfo.InvariantCulture) + "|" + item.Path;
		if (ShellIconCache.TryGetValue(cacheKey, out WeakReference<ImageSource>? weak) && weak.TryGetTarget(out ImageSource? cached))
		{
			item.Icon = cached;
			return;
		}
		CancellationToken token = _navigationCts?.Token ?? CancellationToken.None;
		try
		{
			await IconGate.WaitAsync(token);
			try
			{
				ImageSource? icon = await Task.Run(() => AppInventory.LoadIcon(item.Path, pixels), token);
				if (icon != null && !token.IsCancellationRequested && !_disposed)
				{
					ShellIconCache[cacheKey] = new WeakReference<ImageSource>(icon);
					item.Icon = icon;
				}
			}
			finally { IconGate.Release(); }
		}
		catch (OperationCanceledException) { item.IconRequested = false; }
		catch (Exception ex)
		{
			item.IconRequested = false;
			Logger.Log("Explorer81 icon " + item.Path + ": " + ex.Message);
		}
	}

	private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (FileList.SelectedItem is FileItem item)
		{
			OpenItem(item);
			e.Handled = true;
		}
	}

	private void FileList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		FileItem? item = ItemFromVisual(e.OriginalSource);
		if (item == null) return;
		FileList.SelectedItem = item;
		ShowItemMenu(item, FileList);
		e.Handled = true;
	}

	private static FileItem? ItemFromVisual(object source)
	{
		DependencyObject? current = source as DependencyObject;
		while (current != null)
		{
			if (current is FrameworkElement { DataContext: FileItem item }) return item;
			current = VisualTreeHelper.GetParent(current);
		}
		return null;
	}

	private void ThisPcItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is not Border { DataContext: FileItem item } border) return;
		SelectThisPcItem(border, item);
		if (e.ClickCount >= 2) OpenItem(item);
		e.Handled = true;
	}

	private void ThisPcItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is not Border { DataContext: FileItem item } border) return;
		SelectThisPcItem(border, item);
		ShowItemMenu(item, border);
		e.Handled = true;
	}

	private void SelectThisPcItem(Border border, FileItem item)
	{
		if (_thisPcSelection != null && !ReferenceEquals(_thisPcSelection, border))
		{
			_thisPcSelection.Background = Brushes.Transparent;
			_thisPcSelection.BorderBrush = Brushes.Transparent;
		}
		_thisPcSelection = border;
		_selectedThisPcItem = item;
		border.Background = (Brush)Resources["ExplorerSelected"];
		border.BorderBrush = new SolidColorBrush(Color.FromRgb(44, 150, 201));
		UpdateStatus(_allItems.Count, selectedCount: 1);
	}

	private void ClearThisPcSelection()
	{
		if (_thisPcSelection != null)
		{
			_thisPcSelection.Background = Brushes.Transparent;
			_thisPcSelection.BorderBrush = Brushes.Transparent;
		}
		_thisPcSelection = null;
		_selectedThisPcItem = null;
	}

	private async void OpenItem(FileItem item)
	{
		if (item.IsDir) await NavigateAsync(ExplorerLocation.Folder(item.Path), null, pushHistory: true);
		else FileShell.Open(item.Path);
	}

	private FileItem? SelectedItem() => _current.Kind == LocationKind.ThisPc ? _selectedThisPcItem : FileList.SelectedItem as FileItem;

	private void ShowItemMenu(FileItem item, UIElement target)
	{
		ContextMenu menu = FileContextMenu.Build(item.Path, item.IsDir, () => OpenItem(item));
		ApplyExplorerMenuTheme(menu);
		menu.PlacementTarget = target;
		menu.Placement = PlacementMode.MousePoint;
		menu.IsOpen = true;
	}

	private void ContentHost_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (e.Handled || _current.Kind != LocationKind.Folder || string.IsNullOrWhiteSpace(_current.Path) || ItemFromVisual(e.OriginalSource) != null) return;
		ContextMenu menu = BuildBackgroundMenu();
		menu.PlacementTarget = ContentHost;
		menu.Placement = PlacementMode.MousePoint;
		menu.IsOpen = true;
		e.Handled = true;
	}

	private ContextMenu BuildBackgroundMenu()
	{
		string path = _current.Path!;
		ContextMenu menu = new();
		ApplyExplorerMenuTheme(menu);
		menu.Items.Add(MakeMenuItem("New folder", CreateNewFolder));
		if (DesktopShell.CanPaste()) menu.Items.Add(MakeMenuItem("Paste", () => FileShell.PasteInto(path)));
		menu.Items.Add(new Separator());
		menu.Items.Add(MakeMenuItem("Open command prompt here", () => FileShell.TerminalHere(path, powershell: false)));
		menu.Items.Add(MakeMenuItem("Open PowerShell here", () => FileShell.TerminalHere(path, powershell: true)));
		menu.Items.Add(new Separator());
		menu.Items.Add(MakeMenuItem("Refresh", () => _ = RefreshAsync()));
		return menu;
	}

	private static void ApplyExplorerMenuTheme(ContextMenu menu)
	{
		menu.Background = Brushes.White;
		menu.Foreground = Brushes.Black;
		menu.BorderBrush = new SolidColorBrush(Color.FromRgb(155, 155, 155));
		menu.BorderThickness = new Thickness(1);
		menu.Padding = new Thickness(2);
		Style itemStyle = _explorerMenuItemStyle ??= BuildMenuItemStyle();
		// A ContextMenu ItemContainerStyle is also offered to Separator containers. Keep the
		// Win8.1 styles implicit by target type so mixed menus never throw during popup layout.
		menu.ItemContainerStyle = null;
		menu.Resources[typeof(MenuItem)] = itemStyle;
		Style separatorStyle = _explorerMenuSeparatorStyle ??= BuildMenuSeparatorStyle();
		menu.Resources[typeof(Separator)] = separatorStyle;

		static Style BuildMenuItemStyle()
		{
			Style style = new(typeof(MenuItem));
			style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
			style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
			style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 5, 20, 5)));
			style.Seal();
			return style;
		}

		static Style BuildMenuSeparatorStyle()
		{
			Style style = new(typeof(Separator));
			style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1.0));
			style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3)));
			style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
			style.Seal();
			return style;
		}
	}

	private static MenuItem MakeMenuItem(string header, Action action)
	{
		MenuItem item = new() { Header = header };
		item.Click += (_, _) => TaskbarContextMenu.QueueCommand(action);
		return item;
	}

	private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdateStatus(FileList.Items.Count, FileList.SelectedItems.Count);
	}

	private void UpdateStatus(int itemCount, int selectedCount = 0)
	{
		StatusText.Text = selectedCount > 0 ? $"{selectedCount} item{(selectedCount == 1 ? "" : "s")} selected" : $"{itemCount} item{(itemCount == 1 ? "" : "s")}";
	}

	private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (_viewMode != "Details" || DetailsGridView.Columns.Count < 4) return;
		double width = Math.Max(190, FileList.ActualWidth - 385);
		if (Math.Abs(DetailsGridView.Columns[0].Width - width) > 2) DetailsGridView.Columns[0].Width = width;
	}

	private void ApplyViewMode(string mode, bool persist)
	{
		_viewMode = NormalizeViewMode(mode);
		if (_viewMode == "Icons")
		{
			FileList.View = null;
			FileList.ItemTemplate = (DataTemplate)Resources["LargeIconRowTemplate"];
			FileList.ItemContainerStyle = (Style)Resources["ExplorerIconListItem"];
		}
		else
		{
			FileList.ItemTemplate = null;
			FileList.View = DetailsGridView;
			FileList.ItemContainerStyle = (Style)Resources["ExplorerListItem"];
		}
		foreach (FileItem item in _allItems) if (!item.IsDir) item.IconRequested = false;
		if (persist)
		{
			string durableMode = _viewMode;
			_ = Task.Run(() => SettingsStore.Update(settings => settings.Explorer81ViewMode = durableMode));
		}
	}

	private static string NormalizeViewMode(string? mode) => string.Equals(mode, "Icons", StringComparison.OrdinalIgnoreCase) ? "Icons" : "Details";

	private void DetailsView_Click(object sender, RoutedEventArgs e) => ApplyViewMode("Details", persist: true);

	private void IconsView_Click(object sender, RoutedEventArgs e) => ApplyViewMode("Icons", persist: true);

	private void NavigationPane_Click(object sender, RoutedEventArgs e)
	{
		bool visible = NavigationColumn.Width.Value > 0;
		SetNavigationPaneVisible(!visible, persist: true);
	}

	private void SetNavigationPaneVisible(bool visible, bool persist)
	{
		NavigationColumn.Width = visible ? new GridLength(166) : new GridLength(0);
		NavigationSplitterColumn.Width = visible ? new GridLength(1) : new GridLength(0);
		NavigationTree.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		NavigationSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		if (persist) _ = Task.Run(() => SettingsStore.Update(settings => settings.Explorer81NavigationPane = visible));
	}

	private void AddressBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) != null) return;
		BeginAddressEdit();
		e.Handled = true;
	}

	private static T? FindVisualParent<T>(DependencyObject? value) where T : DependencyObject
	{
		DependencyObject? current = value;
		while (current != null)
		{
			if (current is T match) return match;
			current = VisualTreeHelper.GetParent(current);
		}
		return null;
	}

	private void BeginAddressEdit()
	{
		BreadcrumbHost.Visibility = Visibility.Collapsed;
		AddressBox.Visibility = Visibility.Visible;
		AddressBox.Text = _current.Kind switch { LocationKind.ThisPc => "This PC", LocationKind.Network => "Network", _ => _current.Path ?? "" };
		AddressBox.Focus();
		AddressBox.SelectAll();
	}

	private void EndAddressEdit()
	{
		AddressBox.Visibility = Visibility.Collapsed;
		BreadcrumbHost.Visibility = Visibility.Visible;
	}

	private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			string target = AddressBox.Text;
			EndAddressEdit();
			await NavigateRequestedAsync(target, null, pushHistory: true);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			EndAddressEdit();
			e.Handled = true;
		}
	}

	private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (!AddressBox.IsKeyboardFocusWithin) EndAddressEdit();
	}

	private void FileTab_Click(object sender, RoutedEventArgs e)
	{
		ContextMenu menu = BuildFileMenu();
		menu.PlacementTarget = FileTab;
		menu.Placement = PlacementMode.Bottom;
		menu.IsOpen = true;
	}

	private ContextMenu BuildFileMenu()
	{
		ContextMenu menu = new();
		ApplyExplorerMenuTheme(menu);
		menu.Items.Add(MakeMenuItem("Open new window", () => FileBrowser.Show(_current.Path, null, forceNew: true)));
		if (_current.Kind == LocationKind.Folder && !string.IsNullOrWhiteSpace(_current.Path))
		{
			menu.Items.Add(new Separator());
			menu.Items.Add(MakeMenuItem("Open command prompt", () => FileShell.TerminalHere(_current.Path, powershell: false)));
			menu.Items.Add(MakeMenuItem("Open PowerShell", () => FileShell.TerminalHere(_current.Path, powershell: true)));
		}
		menu.Items.Add(new Separator());
		menu.Items.Add(MakeMenuItem("Close", () => Window.GetWindow(this)?.Close()));
		return menu;
	}

	private void ComputerTab_Click(object sender, RoutedEventArgs e)
	{
		bool switching = ViewCommands.Visibility == Visibility.Visible;
		ComputerCommands.Visibility = Visibility.Visible;
		ViewCommands.Visibility = Visibility.Collapsed;
		SetRibbonExpanded(!_ribbonExpanded || switching);
		ComputerTab.Background = new SolidColorBrush(Color.FromRgb(229, 243, 251));
		ViewTab.Background = Brushes.Transparent;
	}

	private void ViewTab_Click(object sender, RoutedEventArgs e)
	{
		bool switching = ComputerCommands.Visibility == Visibility.Visible;
		ComputerCommands.Visibility = Visibility.Collapsed;
		ViewCommands.Visibility = Visibility.Visible;
		SetRibbonExpanded(!_ribbonExpanded || switching);
		ComputerTab.Background = Brushes.Transparent;
		ViewTab.Background = new SolidColorBrush(Color.FromRgb(229, 243, 251));
	}

	private void CollapseRibbonButton_Click(object sender, RoutedEventArgs e) => SetRibbonExpanded(!_ribbonExpanded);

	private void SetRibbonExpanded(bool expanded)
	{
		if (_ribbonExpanded == expanded) return;
		_ribbonExpanded = expanded;
		TimeSpan duration = Motion.Mode == MotionMode.Off ? TimeSpan.FromMilliseconds(1) : Motion.Time(Motion.Cat.Micro);
		if (expanded)
		{
			RibbonHost.Visibility = Visibility.Visible;
			RibbonHost.BeginAnimation(HeightProperty, new DoubleAnimation(0, 76, duration) { EasingFunction = Motion.Ease(Motion.Cat.Micro) });
			RibbonHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
		}
		else
		{
			DoubleAnimation close = new(RibbonHost.ActualHeight, 0, duration) { EasingFunction = Motion.Ease(Motion.Cat.Micro) };
			close.Completed += (_, _) =>
			{
				if (!_ribbonExpanded)
				{
					RibbonHost.Visibility = Visibility.Collapsed;
					RibbonHost.Height = 0;
				}
			};
			RibbonHost.BeginAnimation(HeightProperty, close);
			RibbonHost.BeginAnimation(OpacityProperty, new DoubleAnimation(RibbonHost.Opacity, 0, duration));
		}
	}

	private void RibbonProperties_Click(object sender, RoutedEventArgs e) => ShowCurrentProperties();

	private void RibbonOpen_Click(object sender, RoutedEventArgs e)
	{
		if (SelectedItem() is FileItem selected) OpenItem(selected);
	}

	private void RibbonNewFolder_Click(object sender, RoutedEventArgs e) => CreateNewFolder();

	private void RibbonRename_Click(object sender, RoutedEventArgs e) => RenameSelected();

	internal void ShowCurrentProperties()
	{
		FileItem? selected = SelectedItem();
		if (selected != null) FileShell.Properties(selected.Path);
		else if (_current.Kind == LocationKind.Folder && !string.IsNullOrWhiteSpace(_current.Path)) FileShell.Properties(_current.Path);
		else FileShell.Open("ms-settings:about");
	}

	internal void CreateNewFolder()
	{
		if (_current.Kind != LocationKind.Folder || string.IsNullOrWhiteSpace(_current.Path)) return;
		string? name = TextPrompt.Show("New folder", "New folder");
		if (string.IsNullOrWhiteSpace(name)) return;
		string? created = FileShell.NewFolder(_current.Path, name);
		if (!string.IsNullOrWhiteSpace(created)) _ = NavigateAsync(_current, created, pushHistory: false);
	}

	private void RenameSelected()
	{
		FileItem? selected = SelectedItem();
		if (selected == null) return;
		string? name = TextPrompt.Show("Rename", System.IO.Path.GetFileName(selected.Path));
		if (!string.IsNullOrWhiteSpace(name) && FileShell.Rename(selected.Path, name)) _ = RefreshAsync();
	}

	private void HelpButton_Click(object sender, RoutedEventArgs e)
	{
		FileShell.Open("https://support.microsoft.com/windows/file-explorer-in-windows-ef370130-1cca-9dc5-e0df-2f7416fe1cb1");
	}

	private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (Keyboard.FocusedElement is TextBox && Keyboard.Modifiers == ModifierKeys.None) return;
		ModifierKeys modifiers = Keyboard.Modifiers;
		if (modifiers == ModifierKeys.Alt && e.Key == Key.Left) { BackButton_Click(BackButton, new RoutedEventArgs()); e.Handled = true; }
		else if (modifiers == ModifierKeys.Alt && e.Key == Key.Right) { ForwardButton_Click(ForwardButton, new RoutedEventArgs()); e.Handled = true; }
		else if (modifiers == ModifierKeys.Alt && e.Key == Key.Up) { await GoUpAsync(); e.Handled = true; }
		else if (modifiers == ModifierKeys.Control && e.Key == Key.L) { BeginAddressEdit(); e.Handled = true; }
		else if (modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
		else if (modifiers == ModifierKeys.Control && e.Key == Key.C && SelectedItem() is FileItem copy) { FileShell.Copy(copy.Path); e.Handled = true; }
		else if (modifiers == ModifierKeys.Control && e.Key == Key.X && SelectedItem() is FileItem cut) { FileShell.Cut(cut.Path); e.Handled = true; }
		else if (modifiers == ModifierKeys.Control && e.Key == Key.V && _current.Kind == LocationKind.Folder && !string.IsNullOrWhiteSpace(_current.Path)) { FileShell.PasteInto(_current.Path); e.Handled = true; }
		else if (modifiers == ModifierKeys.Alt && e.Key == Key.Enter) { ShowCurrentProperties(); e.Handled = true; }
		else if (modifiers == ModifierKeys.None && e.Key == Key.Back) { BackButton_Click(BackButton, new RoutedEventArgs()); e.Handled = true; }
		else if (modifiers == ModifierKeys.None && e.Key == Key.F5) { await RefreshAsync(); e.Handled = true; }
		else if (modifiers == ModifierKeys.None && e.Key == Key.Enter && SelectedItem() is FileItem open) { OpenItem(open); e.Handled = true; }
		else if (modifiers == ModifierKeys.None && e.Key == Key.F2) { RenameSelected(); e.Handled = true; }
		else if (modifiers == ModifierKeys.None && e.Key == Key.Delete && SelectedItem() is FileItem remove) { FileShell.Recycle(remove.Path); e.Handled = true; }
	}

	private void AnimateContent()
	{
		if (!SettingsStore.Current.DeskCompAnimations || Motion.Mode == MotionMode.Off)
		{
			ContentHost.Opacity = 1;
			ContentHost.RenderTransform = Transform.Identity;
			return;
		}
		TranslateTransform slide = new(0, 3);
		ContentHost.RenderTransform = slide;
		ContentHost.Opacity = 0.94;
		Duration duration = Motion.Dur(Motion.Cat.Micro);
		ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = Motion.Ease(Motion.Cat.Micro) });
		slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = Motion.Ease(Motion.Cat.Micro) });
	}

	private static readonly ConcurrentDictionary<string, ImageSource> FolderOverrideCache = new(StringComparer.OrdinalIgnoreCase);

	// User-supplied Win8.1 replacement PNGs (assets/win81icons/uwp/) for the known folders + This PC.
	// Gated by the same "Win8.1 app icons" toggle (Replace81AppIcons) as the Start-tile overrides, so
	// Revert restores the authentic imageres art below (takes effect on the next file-browser navigation).
	private static ImageSource? FolderIconOverride(string name)
	{
		string sem = ShellSemantic(name, out int _);
		string asset = sem switch
		{
			"Shell.Folder" => "Folder81",
			"Shell.Documents" => "Documents_81",
			"Shell.Pictures" => "Pictures_81",
			"Shell.Videos" => "Videos_81",
			"Shell.ThisPC" => "Computer_81",
			_ => null
		};
		if (asset == null)
		{
			return null;
		}
		return FolderOverrideCache.GetOrAdd(asset, a =>
		{
			try
			{
				string file = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Win81Icons", "uwp", a + ".png");
				if (System.IO.File.Exists(file))
				{
					BitmapImage img = new();
					img.BeginInit();
					img.CacheOption = BitmapCacheOption.OnLoad;
					img.UriSource = new Uri(file);
					img.EndInit();
					if (img.CanFreeze) img.Freeze();
					return img;
				}
			}
			catch
			{
			}
			return null;
		});
	}

	private static ImageSource Asset(string name)
	{
		if (SettingsStore.Current.Replace81AppIcons)
		{
			ImageSource ov = FolderIconOverride(name);
			if (ov != null)
			{
				return ov;
			}
		}
		return AssetCache.GetOrAdd(name, key =>
		{
			// AUTHENTIC-FIRST: if this bundled name maps to an extracted Win8.1 library semantic (folder/This PC/known
			// folders/drives), serve the authentic imageres icon from the resolver; fall back to the bundled recreation
			// only if it isn't in the library. Centralised here so file rows, known-folder tiles, nav pane and breadcrumb
			// all get the authentic art from one place. See Win81AssetResolver + assets/Windows81/Shell.
			string sem = ShellSemantic(key, out int px);
			if (sem != null)
			{
				try
				{
					ImageSource authentic = Win81AssetResolver.GetAsset(sem, px);
					if (authentic != null)
					{
						return authentic;
					}
				}
				catch
				{
				}
			}
			string relative = key.Contains('.') && !key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "Assets/win81icons/" + key : "Assets/win81icons/explorer81/" + key;
			BitmapImage image = new();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.UriSource = new Uri("pack://application:,,,/" + relative, UriKind.Absolute);
			image.EndInit();
			if (image.CanFreeze) image.Freeze();
			return image;
		});
	}

	// Maps a bundled explorer asset name ("folder-16.png", "thispc.ico", "documents-48.png", "drive-fixed81.ico", ...) to
	// its authentic Win8.1 library semantic + the requested pixel size. Returns null for names with no authentic equivalent
	// (e.g. desktop, favorites, network, recent's clock-folder), so those keep their bundled recreation.
	private static string ShellSemantic(string name, out int px)
	{
		px = 32;
		int dot = name.LastIndexOf('.');
		string stem = (dot >= 0) ? name.Substring(0, dot) : name;
		string baseName = stem;
		int dash = stem.LastIndexOf('-');
		if (dash >= 0 && int.TryParse(stem.Substring(dash + 1), out int parsed))
		{
			px = parsed;
			baseName = stem.Substring(0, dash);
		}
		switch (baseName)
		{
			case "folder": return "Shell.Folder";
			case "thispc": return "Shell.ThisPC";
			case "documents": return "Shell.Documents";
			case "downloads": return "Shell.Downloads";
			case "music": return "Shell.Music";
			case "pictures": return "Shell.Pictures";
			case "videos": return "Shell.Videos";
			case "drive-fixed81": return "Shell.Drive";
			case "drive-optical81": return "Shell.DriveOptical";
			case "drive-system81": return "Shell.DriveSystem";
			case "drive-usb81": return "Shell.DriveUsb";
			default: return null;
		}
	}

	private static string FormatBytes(long bytes)
	{
		if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " bytes";
		string[] units = { "KB", "MB", "GB", "TB", "PB" };
		double value = bytes;
		int unit = -1;
		do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
		string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
		return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
	}
}
