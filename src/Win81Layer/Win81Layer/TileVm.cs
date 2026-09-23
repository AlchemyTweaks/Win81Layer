using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;

namespace Win81Layer;

public sealed class TileVm : INotifyPropertyChanged
{
	private TileSize _size = TileSize.Medium;

	private readonly HashSet<AppEntry> _watchedMembers = new HashSet<AppEntry>();

	private bool _liveOff;

	private ImageSource? _folderIcon;

	private int _cycleOffset;

	private string _livePrimary = string.Empty;

	private string _liveSecondary = string.Empty;

	private string _liveSymbol = string.Empty;

	private object? _weatherVisual;

	private bool _weatherMotionEnabled;

	private bool _isDragging;

	private bool _isSelected;

	private int _col = -1;

	private int _row = -1;

	private static readonly Dictionary<LiveKind, ImageSource?> _liveIconCache = new Dictionary<LiveKind, ImageSource?>();

	private static readonly object _liveIconGate = new object();

	public required AppEntry Entry { get; init; }

	public LiveKind Live { get; init; } = LiveKind.None;

	public bool LiveOff
	{
		get
		{
			return _liveOff;
		}
		set
		{
			if (_liveOff != value)
			{
				_liveOff = value;
				OnChanged("LiveOff");
				OnChanged("IsLive");
				OnChanged("IsMetroFaceVisible");
				OnChanged("AccessibleName");
			}
		}
	}

	public bool IsLive => Live != LiveKind.None && Live != LiveKind.Desktop && Live != LiveKind.Folder && Live != LiveKind.Photo && !_liveOff;

	public bool IsPhoto => Live == LiveKind.Photo;

	public bool IsWeather => Live == LiveKind.Weather;

	public bool IsMetroLiveTile => Live is LiveKind.Weather or LiveKind.Clock or LiveKind.Agenda or LiveKind.News;

	public object? MetroVisual
	{
		get => _weatherVisual;
		set
		{
			if (!ReferenceEquals(_weatherVisual, value))
			{
				_weatherVisual = value;
				OnChanged("MetroVisual");
				OnChanged("IsMetroFaceVisible");
				OnChanged("AccessibleName");
			}
		}
	}

	public bool MetroMotionEnabled
	{
		get => _weatherMotionEnabled;
		set
		{
			if (_weatherMotionEnabled != value)
			{
				_weatherMotionEnabled = value;
				OnChanged("MetroMotionEnabled");
			}
		}
	}

	public bool IsMetroFaceVisible => IsMetroLiveTile && !LiveOff && MetroVisual != null;

	public string AccessibleName => IsMetroFaceVisible
		? ((MetroTileVisual)MetroVisual!).AccessibleName
		: (Entry?.Name ?? "Tile");

	public string LiveGlyph
	{
		get
		{
			LiveKind live = Live;
			if (1 == 0)
			{
			}
			string result = live switch
			{
				LiveKind.Clock => "\ue823", 
				LiveKind.Calendar => "\ue787", 
				LiveKind.Weather => "\ue9ca", 
				LiveKind.Mail => "\ue715", 
				LiveKind.Agenda => "\ue787", 
				LiveKind.News => "\ue900",
				_ => "\ue10f", 
			};
			if (1 == 0)
			{
			}
			return result;
		}
	}

	// Authentic Win8.1 tile art for the built-in live tiles (transparent white glyph rendered over the brand-colour
	// Root). Only the kinds the user supplied art for; every other kind falls back to the Segoe MDL2 LiveGlyph.
	public ImageSource? LiveIcon
	{
		get
		{
			if (Live == LiveKind.News) return MetroLiveTileArt.NewsLogo;
			// Gated by the "Win8.1 app icons" toggle so the whole custom-icon set reverts together (turn it off -> the
			// tiles fall back to their Segoe MDL2 LiveGlyph). No system state is touched, so it is inherently reversible.
			if (!SettingsStore.Current.Replace81AppIcons)
			{
				return null;
			}
			string asset = Live switch
			{
				LiveKind.Weather => "Live_Weather",
				LiveKind.Agenda => "Live_Agenda",
				LiveKind.Mail => "Live_Mail",
				LiveKind.Clock => "Live_Clock",
				LiveKind.Calendar => "Live_Calendar",
				_ => null,
			};
			if (asset == null)
			{
				return null;
			}
			lock (_liveIconGate)
			{
				if (_liveIconCache.TryGetValue(Live, out ImageSource cached))
				{
					return cached;
				}
				ImageSource img = null;
				try
				{
					string file = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "Win81Icons", "uwp", asset + ".png");
					if (System.IO.File.Exists(file))
					{
						System.Windows.Media.Imaging.BitmapImage bi = new System.Windows.Media.Imaging.BitmapImage();
						bi.BeginInit();
						bi.UriSource = new System.Uri(file);
						bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
						bi.EndInit();
						((System.Windows.Freezable)bi).Freeze();
						img = bi;
					}
				}
				catch
				{
				}
				_liveIconCache[Live] = img;
				return img;
			}
		}
	}

	public bool HasLiveIcon => LiveIcon != null;

	public double LiveIconSize => (double)((Size == TileSize.Small) ? 40 : ((Size == TileSize.Large) ? 120 : 84)) * TileMetrics.Scale;

	// Re-evaluate the live-tile brand colour + icon when the "Win8.1 app icons" toggle flips (live revert).
	public void RefreshLiveAppearance()
	{
		try
		{
			if (Live != LiveKind.None && Live != LiveKind.Desktop && Live != LiveKind.Folder && Live != LiveKind.Photo)
			{
				Entry.TileBrush = LiveTiles.BrandBrush(Live);
			}
		}
		catch
		{
		}
		OnChanged("LiveIcon");
		OnChanged("HasLiveIcon");
	}

	public bool IsDesktop => Live == LiveKind.Desktop;

	// Bound by the DesktopSmallFace overlay in StartScreen.xaml; only shown when IsDesktop && IsSmall.
	// Static-cached in LiveTiles, so this is cheap even though every tile's (collapsed) overlay binds it.
	public System.Windows.Media.ImageSource? DesktopSmallIcon => LiveTiles.DesktopSmallIcon();

	public ObservableCollection<AppEntry> Members { get; } = new ObservableCollection<AppEntry>();

	public bool IsFolder => Live == LiveKind.Folder;

	public ImageSource? FolderIcon
	{
		get
		{
			return _folderIcon;
		}
		private set
		{
			if (_folderIcon != value)
			{
				_folderIcon = value;
				OnChanged("FolderIcon");
			}
		}
	}

	public double FolderFaceSize => (double)((Size == TileSize.Small) ? 44 : ((Size == TileSize.Large) ? 150 : 96)) * TileMetrics.Scale;

	// Size of an OVERRIDE icon face (AppIconOverrides logos: Explorer/Firefox/Office/Notepad/etc.). Previously the
	// OverrideFace Image had no size cap, so it filled the whole tile edge-to-edge (looked huge, esp. on Large).
	// Cap it so the logo sits centred with a Metro margin, prominent but not enormous.
	public double OverrideFaceSize => (double)((Size == TileSize.Small) ? 46 : ((Size == TileSize.Large) ? 178 : 108)) * TileMetrics.Scale;

	public bool FolderIconsPending
	{
		get
		{
			foreach (AppEntry m in Members)
			{
				if (m.Icon == null)
				{
					return true;
				}
			}
			return false;
		}
	}

	public int CyclePhase { get; init; }

	public int Col
	{
		get
		{
			return _col;
		}
		set
		{
			if (_col != value)
			{
				_col = value;
				OnChanged("Col");
			}
		}
	}

	public int Row
	{
		get
		{
			return _row;
		}
		set
		{
			if (_row != value)
			{
				_row = value;
				OnChanged("Row");
			}
		}
	}

	public bool HasCell => _col >= 0 && _row >= 0;

	public bool IsSelected
	{
		get
		{
			return _isSelected;
		}
		set
		{
			if (_isSelected != value)
			{
				_isSelected = value;
				OnChanged("IsSelected");
			}
		}
	}

	public bool IsDragging
	{
		get
		{
			return _isDragging;
		}
		set
		{
			if (_isDragging != value)
			{
				_isDragging = value;
				OnChanged("IsDragging");
			}
		}
	}

	public string LivePrimary
	{
		get
		{
			return _livePrimary;
		}
		set
		{
			if (_livePrimary != value)
			{
				_livePrimary = value;
				OnChanged("LivePrimary");
			}
		}
	}

	public string LiveSecondary
	{
		get
		{
			return _liveSecondary;
		}
		set
		{
			if (_liveSecondary != value)
			{
				_liveSecondary = value;
				OnChanged("LiveSecondary");
			}
		}
	}

	public string LiveSymbol
	{
		get
		{
			return _liveSymbol;
		}
		set
		{
			if (_liveSymbol != value)
			{
				_liveSymbol = value;
				OnChanged("LiveSymbol");
			}
		}
	}

	public TileSize Size
	{
		get
		{
			return _size;
		}
		set
		{
			if (_size != value)
			{
				_size = value;
				string[] array = new string[11] { "Size", "PixelWidth", "PixelHeight", "Cols", "Rows", "IconSize", "IsSmall", "FolderFaceSize", "LiveIconSize", "OverrideFaceSize", "IsMetroFaceVisible" };
				foreach (string p in array)
				{
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
				}
			}
		}
	}

	public int Cols
	{
		get
		{
			TileSize size = Size;
			if (1 == 0)
			{
			}
			int result = size switch
			{
				TileSize.Small => 1, 
				TileSize.Medium => 2, 
				TileSize.Wide => 4, 
				TileSize.Large => 4, 
				_ => 2, 
			};
			if (1 == 0)
			{
			}
			return result;
		}
	}

	public int Rows
	{
		get
		{
			TileSize size = Size;
			if (1 == 0)
			{
			}
			int result = size switch
			{
				TileSize.Small => 1, 
				TileSize.Medium => 2, 
				TileSize.Wide => 2, 
				TileSize.Large => 4, 
				_ => 2, 
			};
			if (1 == 0)
			{
			}
			return result;
		}
	}

	public double PixelWidth => (double)(Cols * 80 - 10) * TileMetrics.Scale;

	public double PixelHeight => (double)(Rows * 80 - 10) * TileMetrics.Scale;

	public bool IsSmall => Size == TileSize.Small;

	public double IconSize => (double)((Size == TileSize.Small) ? 36 : ((Size == TileSize.Large) ? 112 : 76)) * TileMetrics.Scale;

	public event PropertyChangedEventHandler? PropertyChanged;

	public TileVm()
	{
		Members.CollectionChanged += OnMembersChanged;
	}

	private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (e.Action == NotifyCollectionChangedAction.Reset)
		{
			foreach (AppEntry m in _watchedMembers)
			{
				m.PropertyChanged -= OnMemberChanged;
			}
			_watchedMembers.Clear();
		}
		if (e.OldItems != null)
		{
			foreach (AppEntry m2 in e.OldItems)
			{
				if (_watchedMembers.Remove(m2))
				{
					m2.PropertyChanged -= OnMemberChanged;
				}
			}
		}
		if (e.NewItems != null)
		{
			foreach (AppEntry m3 in e.NewItems)
			{
				if (_watchedMembers.Add(m3))
				{
					m3.PropertyChanged += OnMemberChanged;
				}
			}
		}
		if (IsFolder)
		{
			RebuildFolderIcon();
		}
	}

	private bool _folderRebuildPending;

	private void OnMemberChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != "Icon" || !IsFolder)
		{
			return;
		}
		// Composite() only draws the visible window (offset..offset+min(4,count), wrapping). An Icon change on a member
		// outside that window cannot affect the rendered folder tile, so skip the rasterization entirely.
		if (sender is AppEntry entry)
		{
			int count = Members.Count;
			int idx = Members.IndexOf(entry);
			if (count == 0 || idx < 0)
			{
				return;
			}
			int take = System.Math.Min(4, count);
			bool visible = false;
			for (int k = 0; k < take; k++)
			{
				if ((_cycleOffset + k) % count == idx)
				{
					visible = true;
					break;
				}
			}
			if (!visible)
			{
				return;
			}
		}
		// Coalesce: many members' Icons stream in during one QueueIconBatch; collapse the N synchronous RTB renders
		// into a single Background rebuild with an identical final image.
		if (_folderRebuildPending)
		{
			return;
		}
		System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
		if (dispatcher == null)
		{
			RebuildFolderIcon();
			return;
		}
		_folderRebuildPending = true;
		dispatcher.BeginInvoke((System.Action)delegate
		{
			_folderRebuildPending = false;
			RebuildFolderIcon();
		}, System.Windows.Threading.DispatcherPriority.Background);
	}

	public void RebuildFolderIcon()
	{
		if (IsFolder)
		{
			FolderIcon = FolderTiles.Composite(Members, _cycleOffset);
		}
	}

	public void AdvanceFolderCycle()
	{
		if (IsFolder && Members.Count > 4)
		{
			_cycleOffset = (_cycleOffset + 4) % Members.Count;
			RebuildFolderIcon();
		}
	}

	private void OnChanged(string p)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
	}
}
