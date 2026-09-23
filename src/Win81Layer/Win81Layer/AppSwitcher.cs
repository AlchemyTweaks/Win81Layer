using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class AppSwitcher : Window
{
	private sealed record EntryInfo(Border Entry, nint Hwnd, string? Exe);

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct DWM_THUMBNAIL_PROPERTIES
	{
		public int dwFlags;

		public RECT rcDestination;

		public RECT rcSource;

		public byte opacity;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fVisible;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fSourceClientAreaOnly;
	}

	private const double PanelWidth = 200.0;

	private const double ThumbWidth = 180.0;

	private const double LabelH = 30.0;

	private readonly StackPanel _list;

	private readonly ScrollViewer _root;

	private readonly DockPanel _slideRoot;

	private readonly System.Windows.Controls.TextBox _search;

	private readonly TextBlock _searchHint;

	private DispatcherTimer? _filterTimer;

	private DispatcherTimer? _hideFallback;

	private readonly List<nint> _thumbs;

	private readonly List<(Border Box, nint Hwnd)> _boxes;

	private readonly List<EntryInfo> _entries;

	private readonly DispatcherTimer _proximity;

	private SwitcherState _state;

	private nint _hwnd;

	private bool _hiding;

	private int _awayTicks;

	private double _sx;

	private double _sy;

	private EntryInfo? _dragInfo;

	private bool _dragging;

	private double _dragStartY;

	private bool _snapMode;

	private Rectangle _snapTarget;

	private SnapOverlay? _snapOverlay;

	private bool _menuOpen;

	private bool _keyboardMode;

	private int _selectedIndex;

	private int _transitionGeneration;

	private SolidColorBrush _panelBrush;

	private SolidColorBrush _labelBrush;

	private SolidColorBrush _thumbBrush;

	private SolidColorBrush _hoverBrush;

	private SolidColorBrush _textBrush;

	// Bottom-pinned Win8.1 "Start" button (docked to _slideRoot, NOT inside the scrolling _list). Built once; its bar/
	// text brushes are refreshed by SyncPalette, and the glyph recolours to the theme accent while pressed.
	private Border? _startBar;

	private TextBlock? _startText;

	private SolidColorBrush? _startGlyphBrush;

	public Action<string?, string>? PinToStartRequested;

	// Raised when the user clicks the bottom Start button; the owner (App) wires this to open the Start screen.
	public Action? StartRequested;

	private const int DWM_TNP_RECTDESTINATION = 1;

	private const int DWM_TNP_VISIBLE = 8;

	private const int DWM_TNP_OPACITY = 4;

	private const int DWM_TNP_SOURCECLIENTAREAONLY = 16;

	private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

	private const int DWMWA_BORDER_COLOR = 34;

	private const int DWMWCP_DONOTROUND = 1;

	private const int DWM_COLOR_NONE = -2;

	private const int GWL_STYLE = -16;

	private const long NATIVE_FRAME_STYLES = 0x00C40000L;

	private const uint SWP_FRAME_REFRESH = 0x0037u;

	private const int HIDDEN_NATIVE_COORDINATE = -32000;

	private const uint SWP_PARK = 0x0015u;

	public bool IsShown => base.IsVisible && !_hiding;

	public AppSwitcher()
	{
		//IL_0461: Unknown result type (might be due to invalid IL or missing references)
		//IL_0466: Unknown result type (might be due to invalid IL or missing references)
		//IL_047d: Expected O, but got Unknown
		_thumbs = new List<nint>();
		_boxes = new List<(Border, nint)>();
		_entries = new List<EntryInfo>();
		_state = new SwitcherState();
		_sx = 1.0;
		_sy = 1.0;
		_selectedIndex = -1;
		_panelBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 27, 27));
		_labelBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17));
		_thumbBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 42, 42));
		_hoverBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85));
		_textBrush = new SolidColorBrush(Colors.White);
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Topmost = true;
		base.ShowActivated = false;
		base.AllowsTransparency = false;
		base.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 27, 27));
		base.BorderBrush = System.Windows.Media.Brushes.Transparent;
		base.BorderThickness = new Thickness(0.0);
		base.Width = 200.0;
		base.UseLayoutRounding = true;
		base.SnapsToDevicePixels = true;
		base.WindowStartupLocation = WindowStartupLocation.Manual;
		base.Title = "App switcher";
		_list = new StackPanel
		{
			Margin = new Thickness(0.0, 2.0, 0.0, 8.0)
		};
		_root = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Content = _list
		};
		StyleSwitcherScrollBar();
		_search = new System.Windows.Controls.TextBox
		{
			Height = 30.0,
			Margin = new Thickness(8.0, 8.0, 8.0, 4.0),
			Padding = new Thickness(6.0, 3.0, 6.0, 3.0),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalContentAlignment = VerticalAlignment.Center,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 42, 42)),
			Foreground = System.Windows.Media.Brushes.White,
			CaretBrush = System.Windows.Media.Brushes.White,
			BorderThickness = new Thickness(1.0),
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(102, byte.MaxValue, byte.MaxValue, byte.MaxValue))
		};
		_search.TextChanged += delegate
		{
			ScheduleFilter();
		};
		_searchHint = new TextBlock
		{
			Text = "Search open windows",
			IsHitTestVisible = false,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(153, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			Margin = new Thickness(16.0, 8.0, 8.0, 4.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid searchHost = new Grid
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 0.0)
		};
		searchHost.Children.Add(_search);
		searchHost.Children.Add(_searchHint);
		_slideRoot = new DockPanel
		{
			LastChildFill = true
		};
		DockPanel.SetDock(searchHost, Dock.Top);
		_slideRoot.Children.Add(searchHost);
		BuildStartBar();
		DockPanel.SetDock(_startBar!, Dock.Bottom);
		_slideRoot.Children.Add(_startBar!);   // pinned bottom-left; MUST be added before the fill child (_root)
		_slideRoot.Children.Add(_root);
		base.Content = _slideRoot;
		base.SourceInitialized += delegate
		{
			_hwnd = new WindowInteropHelper(this).Handle;
			ConfigureNativeSurface();
		};
		base.Loaded += delegate
		{
			if (_hwnd == IntPtr.Zero)
			{
				_hwnd = new WindowInteropHelper(this).Handle;
			}
			ConfigureNativeSurface();
		};
		base.MouseLeave += delegate
		{
			if (!_hiding && base.IsVisible && (object)_dragInfo == null && !_menuOpen)
			{
				HideSwitcher();
			}
		};
		base.PreviewKeyDown += OnSwitcherKey;
		base.MouseMove += OnSwitcherMove;
		base.MouseLeftButtonUp += OnSwitcherUp;
		_proximity = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(250L)
		};
		_proximity.Tick += delegate
		{
			ProximityCheck();
		};
	}

	public void ShowSwitcher(bool keyboard = false)
	{
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		if (IsShown)
		{
			return;
		}
		_transitionGeneration++;
		StopHideFallback();
		_hiding = false;
		_keyboardMode = keyboard;
		_selectedIndex = -1;
		Screen screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		Rectangle b = screen.Bounds;
		double provisionalSx = _sx > 0.0 ? _sx : 1.0;
		double provisionalSy = _sy > 0.0 ? _sy : 1.0;
		base.Height = (double)b.Height / provisionalSy;
		base.Top = (double)b.Top / provisionalSy;
		base.Left = (double)b.Left / provisionalSx;
		// The transform must already be off-screen before Show creates/presents the HWND. Otherwise a busy
		// compositor can expose one frame of the system scrollbar or the native left border at x=0.
		TranslateTransform t = new TranslateTransform(0.0 - base.Width - 4.0, 0.0);
		_slideRoot.RenderTransform = t;
		if (_hwnd == IntPtr.Zero)
		{
			_hwnd = new WindowInteropHelper(this).EnsureHandle();
		}
		ConfigureNativeSurface();
		Show();
		ConfigureNativeSurface();
		PresentationSource src = PresentationSource.FromVisual(this);
		double? num;
		Matrix transformToDevice;
		if (src == null)
		{
			num = null;
		}
		else
		{
			CompositionTarget compositionTarget = src.CompositionTarget;
			if (compositionTarget == null)
			{
				num = null;
			}
			else
			{
				transformToDevice = compositionTarget.TransformToDevice;
				num = transformToDevice.M11;
			}
		}
		_sx = num ?? 1.0;
		double? num2;
		if (src == null)
		{
			num2 = null;
		}
		else
		{
			CompositionTarget compositionTarget2 = src.CompositionTarget;
			if (compositionTarget2 == null)
			{
				num2 = null;
			}
			else
			{
				transformToDevice = compositionTarget2.TransformToDevice;
				num2 = transformToDevice.M22;
			}
		}
		_sy = num2 ?? 1.0;
		if (_sx <= 0.0)
		{
			_sx = 1.0;
		}
		if (_sy <= 0.0)
		{
			_sy = 1.0;
		}
		base.Height = (double)b.Height / _sy;
		base.Top = (double)b.Top / _sy;
		base.Left = (double)b.Left / _sx;
		SyncPalette();
		_search.Clear();
		DispatcherTimer? filterTimer = _filterTimer;
		if (filterTimer != null)
		{
			filterTimer.Stop();
		}
		_searchHint.Visibility = Visibility.Visible;
		BuildEntries();
		UpdateLayout();
		_awayTicks = 0;
		_proximity.Start();
		double hiddenX = HiddenOffset();
		t.BeginAnimation(TranslateTransform.XProperty, null);
		t.X = hiddenX;
		DoubleAnimation slideIn = new DoubleAnimation(hiddenX, 0.0, Motion.Dur(Motion.Cat.SwitcherIn))
		{
			EasingFunction = Motion.Ease(Motion.Cat.SwitcherIn)
		};
		slideIn.Completed += delegate
		{
			if (IsShown)
			{
				Unregister();
				Register();
			}
		};
		t.BeginAnimation(TranslateTransform.XProperty, slideIn);
		if (keyboard)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				Activate();
				_search.Focus();
				SelectIndex((_list.Children.Count > 1) ? 1 : 0);
			}, (DispatcherPriority)5, Array.Empty<object>());
		}
		Logger.Log("AppSwitcher: show");
	}

	public void HideSwitcher()
	{
		if (_hiding || !base.IsVisible)
		{
			_proximity.Stop();
			return;
		}
		_hiding = true;
		int generation = ++_transitionGeneration;
		StopHideFallback();
		_proximity.Stop();
		DispatcherTimer? filterTimer = _filterTimer;
		if (filterTimer != null)
		{
			filterTimer.Stop();
		}
		_snapMode = false;
		_keyboardMode = false;
		_selectedIndex = -1;
		_snapOverlay?.HideZone();
		TranslateTransform t = new TranslateTransform(0.0, 0.0);
		_slideRoot.RenderTransform = t;
		double hiddenX = HiddenOffset();
		DoubleAnimation slide = new DoubleAnimation(0.0, hiddenX, Motion.Dur(Motion.Cat.SwitcherOut))
		{
			EasingFunction = Motion.Ease(Motion.Cat.SwitcherOut)
		};
		slide.Completed += delegate
		{
			CompleteHide(generation, t, hiddenX);
		};
		t.BeginAnimation(TranslateTransform.XProperty, slide);
		_hideFallback = new DispatcherTimer
		{
			Interval = Motion.Time(Motion.Cat.SwitcherOut) + TimeSpan.FromMilliseconds(80.0)
		};
		_hideFallback.Tick += delegate
		{
			CompleteHide(generation, t, hiddenX);
		};
		_hideFallback.Start();
	}

	private double HiddenOffset()
	{
		// Four physical pixels cover WPF/DWM rounding at every supported DPI. Moving exactly Width DIPs
		// could leave the final 1-2 device pixels of the scrollbar/native border on the monitor edge.
		double scale = _sx > 0.0 ? _sx : 1.0;
		return 0.0 - base.Width - 4.0 / scale;
	}

	private void CompleteHide(int generation, TranslateTransform transform, double hiddenX)
	{
		if (!_hiding || generation != _transitionGeneration)
		{
			return;
		}
		StopHideFallback();
		Unregister();
		transform.BeginAnimation(TranslateTransform.XProperty, null);
		transform.X = hiddenX;
		ParkNativeSurfaceOffscreen();
		Hide();
		_hiding = false;
	}

	private void StopHideFallback()
	{
		if (_hideFallback == null)
		{
			return;
		}
		_hideFallback.Stop();
		_hideFallback = null;
	}

	public void Toggle()
	{
		if (IsShown)
		{
			HideSwitcher();
		}
		else
		{
			ShowSwitcher();
		}
	}

	public void CycleOrShow()
	{
		if (IsShown)
		{
			Activate();
			_keyboardMode = true;
			MoveSelection(1);
		}
		else
		{
			ShowSwitcher(keyboard: true);
		}
	}

	private void OnSwitcherKey(object sender, System.Windows.Input.KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Invalid comparison between Unknown and I4
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Invalid comparison between Unknown and I4
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Invalid comparison between Unknown and I4
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Invalid comparison between Unknown and I4
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Invalid comparison between Unknown and I4
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Invalid comparison between Unknown and I4
		Key key = e.Key;
		Key val = key;
		if ((int)val <= 6)
		{
			if ((int)val != 3)
			{
				if ((int)val == 6)
				{
					ActivateSelected();
					e.Handled = true;
				}
			}
			else
			{
				MoveSelection((((int)Keyboard.Modifiers & 4) == 0) ? 1 : (-1));
				e.Handled = true;
			}
		}
		else if ((int)val != 13)
		{
			if ((int)val != 24)
			{
				if ((int)val == 26)
				{
					MoveSelection(1);
					e.Handled = true;
				}
			}
			else
			{
				MoveSelection(-1);
				e.Handled = true;
			}
		}
		else
		{
			HideSwitcher();
			e.Handled = true;
		}
	}

	private void SelectIndex(int i)
	{
		_keyboardMode = true;
		_selectedIndex = i;
		for (int k = 0; k < _list.Children.Count; k++)
		{
			if (_list.Children[k] is Border b)
			{
				b.BorderBrush = ((k == i) ? _hoverBrush : System.Windows.Media.Brushes.Transparent);
			}
		}
		if (i >= 0 && i < _list.Children.Count && _list.Children[i] is FrameworkElement fe)
		{
			fe.BringIntoView();
		}
	}

	private void MoveSelection(int delta)
	{
		int n = _list.Children.Count;
		if (n == 0)
		{
			return;
		}
		int i = ((_selectedIndex >= 0) ? _selectedIndex : 0);
		for (int step = 0; step < n; step++)
		{
			i = ((i + delta) % n + n) % n;
			if (_list.Children[i] is FrameworkElement { Visibility: Visibility.Visible })
			{
				SelectIndex(i);
				break;
			}
		}
	}

	private void ScheduleFilter()
	{
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Expected O, but got Unknown
		_searchHint.Visibility = ((!string.IsNullOrEmpty(_search.Text)) ? Visibility.Collapsed : Visibility.Visible);
		if (_filterTimer == null)
		{
			_filterTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(110L)
			};
		}
		_filterTimer.Tick -= OnFilterTick;
		_filterTimer.Tick += OnFilterTick;
		_filterTimer.Stop();
		_filterTimer.Start();
	}

	private void OnFilterTick(object? s, EventArgs e)
	{
		DispatcherTimer? filterTimer = _filterTimer;
		if (filterTimer != null)
		{
			filterTimer.Stop();
		}
		ApplyFilter();
	}

	private void ApplyFilter()
	{
		if (!IsShown)
		{
			return;
		}
		string q = _search.Text?.Trim() ?? "";
		foreach (EntryInfo info in _entries)
		{
			string title = WindowList.GetTitle(info.Hwnd) ?? "";
			int num;
			if (q.Length != 0 && !title.Contains(q, StringComparison.CurrentCultureIgnoreCase))
			{
				string exe = info.Exe;
				num = ((exe != null && Path.GetFileNameWithoutExtension(exe).Contains(q, StringComparison.CurrentCultureIgnoreCase)) ? 1 : 0);
			}
			else
			{
				num = 1;
			}
			bool match = (byte)num != 0;
			info.Entry.Visibility = ((!match) ? Visibility.Collapsed : Visibility.Visible);
		}
		UpdateLayout();
		Unregister();
		Register();
		if (_keyboardMode)
		{
			SelectFirstVisible();
		}
	}

	private void SelectFirstVisible()
	{
		for (int i = 1; i < _list.Children.Count; i++)
		{
			if (_list.Children[i] is FrameworkElement { Visibility: Visibility.Visible })
			{
				SelectIndex(i);
				return;
			}
		}
		SelectIndex(0);
	}

	private void ActivateSelected()
	{
		if (_selectedIndex >= 0 && _selectedIndex < _list.Children.Count)
		{
			UIElement el = _list.Children[_selectedIndex];
			EntryInfo info = _entries.FirstOrDefault((EntryInfo x) => x.Entry == el);
			if ((object)info != null)
			{
				WindowList.Activate(info.Hwnd);
				HideSwitcher();
			}
			else
			{
				GoToDesktop();
			}
		}
	}

	private void BeginPress(EntryInfo info, MouseButtonEventArgs e)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		_dragInfo = info;
		_dragging = false;
		System.Windows.Point position = e.GetPosition(_list);
		_dragStartY = position.Y;
		CaptureMouse();
	}

	private void OnSwitcherMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		if ((object)_dragInfo == null || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		System.Windows.Point position = e.GetPosition(_list);
		double y = position.Y;
		if (!_dragging)
		{
			if (Math.Abs(y - _dragStartY) < 6.0)
			{
				return;
			}
			_dragging = true;
			Unregister();
			_dragInfo.Entry.Opacity = 0.55;
		}
		position = e.GetPosition(this);
		if (position.X > base.ActualWidth)
		{
			_snapMode = true;
			System.Drawing.Point cur = System.Windows.Forms.Cursor.Position;
			_snapTarget = SnapZones.Compute(cur, out string _);
			ShowSnapPreview(_snapTarget);
			return;
		}
		if (_snapMode)
		{
			_snapMode = false;
			_snapOverlay?.HideZone();
		}
		ReorderTo(y);
	}

	private void OnSwitcherUp(object sender, MouseButtonEventArgs e)
	{
		if ((object)_dragInfo != null)
		{
			ReleaseMouseCapture();
			EntryInfo info = _dragInfo;
			bool wasDragging = _dragging;
			bool snap = _snapMode;
			_dragInfo = null;
			_dragging = false;
			_snapMode = false;
			_snapOverlay?.HideZone();
			if (snap)
			{
				SnapZones.Apply(info.Hwnd, _snapTarget);
				SnapManager.SetScale(_sx, _sy);
				SnapManager.Record(info.Hwnd, _snapTarget);
				WindowList.Activate(info.Hwnd);
				HideSwitcher();
			}
			else if (wasDragging)
			{
				info.Entry.Opacity = 1.0;
				CommitOrder();
				UpdateLayout();
				Register();
			}
			else
			{
				WindowList.Activate(info.Hwnd);
				HideSwitcher();
			}
		}
	}

	private void ShowSnapPreview(Rectangle physical)
	{
		if (_snapOverlay == null)
		{
			_snapOverlay = new SnapOverlay(_sx, _sy);
		}
		_snapOverlay.ShowZone(physical, SafeAccent());
	}

	private void ReorderTo(double y)
	{
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		Border dragged = _dragInfo.Entry;
		int above = 0;
		for (int i = 1; i < _list.Children.Count; i++)
		{
			FrameworkElement child = (FrameworkElement)_list.Children[i];
			if (child != dragged && child.Visibility == Visibility.Visible)
			{
				System.Windows.Point val = child.TranslatePoint(new System.Windows.Point(0.0, 0.0), _list);
				double top = val.Y;
				if (y >= top + child.ActualHeight / 2.0)
				{
					above++;
				}
			}
		}
		int desired = 1 + above;
		int cur = _list.Children.IndexOf(dragged);
		if (cur != desired)
		{
			_list.Children.Remove(dragged);
			if (desired > _list.Children.Count)
			{
				desired = _list.Children.Count;
			}
			if (desired < 1)
			{
				desired = 1;
			}
			_list.Children.Insert(desired, dragged);
		}
	}

	private void CommitOrder()
	{
		List<string> exes = new List<string>();
		for (int i = 1; i < _list.Children.Count; i++)
		{
			UIElement child = _list.Children[i];
			EntryInfo info = _entries.FirstOrDefault((EntryInfo x) => x.Entry == child);
			if ((object)info != null)
			{
				exes.Add(info.Exe);
			}
		}
		_state.SetOrder(exes);
	}

	private void RebuildNow()
	{
		if (IsShown)
		{
			BuildEntries();
			UpdateLayout();
			if (!string.IsNullOrEmpty(_search.Text))
			{
				ApplyFilter();
			}
			else
			{
				Register();
			}
		}
	}

	private void BuildEntries()
	{
		Unregister();
		_list.Children.Clear();
		_boxes.Clear();
		_entries.Clear();
		_state = SwitcherState.Load();
		_list.Children.Add(DesktopEntry());
		uint ownPid = (uint)Environment.ProcessId;
		List<(nint, string, string)> wins = new List<(nint, string, string)>();
		foreach (var (hwnd, title) in WindowList.Enumerate(_hwnd))
		{
			GetWindowThreadProcessId(hwnd, out var pid);
			if (pid != ownPid)
			{
				wins.Add((hwnd, title, WindowList.GetExePath(hwnd)));
			}
		}
		IEnumerable<(nint, string, string)> sorted = from x in wins.Select<(nint, string, string), ((nint, string, string), int)>(((nint Hwnd, string Title, string Exe) w, int i) => (w: w, i: i))
			orderby (!_state.IsPinned(x.Item1.Item3)) ? 1 : 0, _state.OrderIndex(x.Item1.Item3), x.Item2
			select x.Item1;
		foreach (var (hwnd2, title2, exe) in sorted)
		{
			_list.Children.Add(WindowEntry(hwnd2, title2, exe));
		}
	}

	private FrameworkElement WindowEntry(nint hwnd, string title, string? exe)
	{
		Border thumbBox = new Border
		{
			Height = ThumbHeight(hwnd),
			Background = _thumbBrush
		};
		Grid label = new Grid
		{
			Height = 30.0,
			Background = _labelBrush
		};
		StackPanel titleRow = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(9.0, 0.0, 60.0, 0.0)
		};
		ImageSource icon = IconResolver.EnsureNonBlank(WindowList.GetIcon(hwnd, preferLarge: false), title);
		titleRow.Children.Add(new System.Windows.Controls.Image
		{
			Source = icon,
			Width = 16.0,
			Height = 16.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		});
		titleRow.Children.Add(new TextBlock
		{
			Text = title,
			Foreground = _textBrush,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			MaxWidth = ((icon != null) ? 158 : 182),
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		label.Children.Add(titleRow);
		System.Windows.Controls.Button close = new System.Windows.Controls.Button
		{
			Content = "\ue8bb",
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 9.0,
			Width = 28.0,
			Height = 30.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Template = IconButtonTemplate(),
			Visibility = Visibility.Hidden
		};
		close.Click += delegate
		{
			WindowList.Close(hwnd);
			RebuildSoon();
		};
		bool pinned = _state.IsPinned(exe);
		System.Windows.Controls.Button pin = new System.Windows.Controls.Button
		{
			Content = "\ue718",
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 12.0,
			Width = 28.0,
			Height = 30.0,
			Foreground = (pinned ? new SolidColorBrush(SafeAccent()) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200))),
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Template = IconButtonTemplate(),
			Visibility = ((!pinned) ? Visibility.Hidden : Visibility.Visible),
			ToolTip = (pinned ? "Unpin from switcher" : "Pin to switcher")
		};
		pin.Click += delegate
		{
			bool flag = !_state.IsPinned(exe);
			_state.TogglePin(exe);
			ToastService.Show(icon, "App switcher", flag ? "Pinned" : "Unpinned", flag ? (title + " stays at the top of the switcher.") : (title + " removed from favourites."));
			RebuildNow();
		};
		StackPanel rightButtons = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center
		};
		rightButtons.Children.Add(pin);
		rightButtons.Children.Add(close);
		label.Children.Add(rightButtons);
		StackPanel stack = new StackPanel();
		stack.Children.Add(thumbBox);
		stack.Children.Add(label);
		Border entry = new Border
		{
			Margin = new Thickness(8.0, 1.0, 8.0, 1.0),
			BorderBrush = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(2.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Child = stack
		};
		entry.MouseEnter += delegate
		{
			entry.BorderBrush = _hoverBrush;
			pin.Visibility = Visibility.Visible;
			close.Visibility = Visibility.Visible;
		};
		entry.MouseLeave += delegate
		{
			entry.BorderBrush = System.Windows.Media.Brushes.Transparent;
			if (!_state.IsPinned(exe))
			{
				pin.Visibility = Visibility.Hidden;
			}
			close.Visibility = Visibility.Hidden;
		};
		EntryInfo info = new EntryInfo(entry, hwnd, exe);
		entry.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			BeginPress(info, e);
		};
		entry.MouseRightButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			ShowEntryMenu(info);
			e.Handled = true;
		};
		entry.MouseDown += delegate(object _, MouseButtonEventArgs e)
		{
			if (e.ChangedButton == MouseButton.Middle)
			{
				WindowList.Close(hwnd);
				RebuildSoon();
				e.Handled = true;
			}
		};
		_boxes.Add((thumbBox, hwnd));
		_entries.Add(info);
		return entry;
	}

	private FrameworkElement DesktopEntry()
	{
		Border thumbBox = new Border
		{
			Height = 120.0,
			Background = _thumbBrush
		};
		try
		{
			string wp = TaskbarTheme.WallpaperPath();
			if (!string.IsNullOrEmpty(wp) && File.Exists(wp))
			{
				BitmapImage bmp = new BitmapImage();
				bmp.BeginInit();
				bmp.CacheOption = BitmapCacheOption.OnLoad;
				bmp.DecodePixelWidth = 300;
				bmp.UriSource = new Uri(wp);
				bmp.EndInit();
				((Freezable)bmp).Freeze();
				thumbBox.Background = new ImageBrush(bmp)
				{
					Stretch = Stretch.UniformToFill
				};
			}
		}
		catch
		{
		}
		Grid label = new Grid
		{
			Height = 30.0,
			Background = _labelBrush
		};
		StackPanel deskRow = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(9.0, 0.0, 8.0, 0.0)
		};
		deskRow.Children.Add(new TextBlock
		{
			Text = "\ue7f4",
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 14.0,
			Foreground = _textBrush,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		});
		deskRow.Children.Add(new TextBlock
		{
			Text = "Desktop",
			Foreground = _textBrush,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		label.Children.Add(deskRow);
		StackPanel stack = new StackPanel();
		stack.Children.Add(thumbBox);
		stack.Children.Add(label);
		Border entry = new Border
		{
			Margin = new Thickness(8.0, 1.0, 8.0, 1.0),
			BorderBrush = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(2.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Child = stack
		};
		entry.MouseEnter += delegate
		{
			entry.BorderBrush = _hoverBrush;
		};
		entry.MouseLeave += delegate
		{
			entry.BorderBrush = System.Windows.Media.Brushes.Transparent;
		};
		entry.MouseLeftButtonUp += delegate
		{
			GoToDesktop();
			HideSwitcher();
		};
		return entry;
	}

	// The Win8.1 bottom-left "Start" button. Uses the EXACT taskbar Start logo (the start81 four-pane flag PNG as an
	// OpacityMask over a recolourable rectangle) so it matches the taskbar; resting white, turns the theme accent while
	// pressed, then dismisses the switcher and opens the Start screen via StartRequested.
	private void BuildStartBar()
	{
		_startGlyphBrush = new SolidColorBrush(System.Windows.Media.Colors.White);
		System.Windows.Shapes.Rectangle glyph = new System.Windows.Shapes.Rectangle
		{
			Width = 24.0,
			Height = 24.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Fill = _startGlyphBrush
		};
		try
		{
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.UriSource = new Uri("pack://application:,,,/Assets/start81_32.png", UriKind.Absolute);
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.EndInit();
			((Freezable)bmp).Freeze();
			glyph.OpacityMask = new ImageBrush(bmp)
			{
				Stretch = Stretch.Uniform
			};
		}
		catch
		{
		}
		_startText = new TextBlock
		{
			Text = "Start",
			Foreground = _textBrush,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0)
		};
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(9.0, 0.0, 8.0, 0.0)
		};
		row.Children.Add(glyph);
		row.Children.Add(_startText);
		_startBar = new Border
		{
			Height = 48.0,
			Background = _labelBrush,
			Cursor = System.Windows.Input.Cursors.Hand,
			Child = row,
			ToolTip = "Start"
		};
		System.Windows.Automation.AutomationProperties.SetName(_startBar, "Start");
		_startBar.MouseEnter += delegate
		{
			if (_startBar != null)
			{
				_startBar.Background = _hoverBrush;
			}
		};
		_startBar.MouseLeave += delegate
		{
			if (_startBar != null)
			{
				_startBar.Background = _labelBrush;
			}
			if (_startGlyphBrush != null)
			{
				_startGlyphBrush.Color = System.Windows.Media.Colors.White;
			}
		};
		_startBar.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			if (_startGlyphBrush != null)
			{
				_startGlyphBrush.Color = StartAccent.Color();   // pressed -> theme accent
			}
			e.Handled = true;
		};
		_startBar.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			if (_startGlyphBrush != null)
			{
				_startGlyphBrush.Color = System.Windows.Media.Colors.White;
			}
			e.Handled = true;
			HideSwitcher();
			StartRequested?.Invoke();
		};
	}

	// The Start bar is built once (unlike the entries, which are rebuilt every show), so keep its bar/text brushes in
	// step whenever SyncPalette swaps the palette (normal vs high-contrast). Hover/press read the live fields at event time.
	private void RefreshStartBarPalette()
	{
		if (_startBar != null)
		{
			_startBar.Background = _labelBrush;
		}
		if (_startText != null)
		{
			_startText.Foreground = _textBrush;
		}
	}

	private void Register()
	{
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		foreach (var (box, srcHwnd) in _boxes)
		{
			try
			{
				if (!(box.ActualWidth < 1.0) && !(box.ActualHeight < 1.0) && DwmRegisterThumbnail(_hwnd, srcHwnd, out var thumb) == 0)
				{
					_thumbs.Add(thumb);
					System.Windows.Point tl = box.TransformToVisual(this).Transform(new System.Windows.Point(0.0, 0.0));
					RECT rc = new RECT
					{
						Left = (int)Math.Round(tl.X * _sx),
						Top = (int)Math.Round(tl.Y * _sy),
						Right = (int)Math.Round((tl.X + box.ActualWidth) * _sx),
						Bottom = (int)Math.Round((tl.Y + box.ActualHeight) * _sy)
					};
					DWM_THUMBNAIL_PROPERTIES props = new DWM_THUMBNAIL_PROPERTIES
					{
						dwFlags = 29,
						rcDestination = rc,
						opacity = byte.MaxValue,
						fVisible = true,
						fSourceClientAreaOnly = false
					};
					DwmUpdateThumbnailProperties(thumb, ref props);
				}
			}
			catch
			{
			}
		}
	}

	private void Unregister()
	{
		foreach (nint t in _thumbs)
		{
			try
			{
				DwmUnregisterThumbnail(t);
			}
			catch
			{
			}
		}
		_thumbs.Clear();
	}

	private void RebuildSoon()
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Expected O, but got Unknown
		DispatcherTimer t = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(180L)
		};
		t.Tick += delegate
		{
			t.Stop();
			if (IsShown)
			{
				BuildEntries();
				UpdateLayout();
				if (!string.IsNullOrEmpty(_search.Text))
				{
					ApplyFilter();
				}
				else
				{
					Register();
				}
			}
		};
		t.Start();
	}

	private void ShowEntryMenu(EntryInfo info)
	{
		nint hwnd = info.Hwnd;
		System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
		{
			Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
		};
		TaskbarContextMenu.ApplyTheme(menu);
		menu.Items.Add(TaskbarContextMenu.Leaf(WindowList.IsMaximized(hwnd) ? "Restore" : "Maximize", delegate
		{
			WindowList.ToggleMaximize(hwnd);
			HideSwitcher();
		}));
		menu.Items.Add(TaskbarContextMenu.Leaf("Minimize", delegate
		{
			WindowList.Minimize(hwnd);
			HideSwitcher();
		}));
		menu.Items.Add(TaskbarContextMenu.Leaf("New window", delegate
		{
			WindowList.NewWindow(hwnd);
			HideSwitcher();
		}));
		menu.Items.Add(TaskbarContextMenu.Leaf("Pin to Start", delegate
		{
			PinToStartRequested?.Invoke(WindowList.GetExePath(hwnd), WindowList.GetTitle(hwnd));
			HideSwitcher();
		}));
		menu.Items.Add(TaskbarContextMenu.Sep());
		menu.Items.Add(TaskbarContextMenu.Leaf("Close", delegate
		{
			WindowList.Close(hwnd);
			RebuildSoon();
		}));
		_menuOpen = true;
		menu.Closed += delegate
		{
			_menuOpen = false;
		};
		menu.PlacementTarget = info.Entry;
		menu.Placement = PlacementMode.MousePoint;
		menu.IsOpen = true;
	}

	private void ProximityCheck()
	{
		if (_menuOpen || (object)_dragInfo != null)
		{
			_awayTicks = 0;
			return;
		}
		if (base.IsMouseOver)
		{
			_awayTicks = 0;
			return;
		}
		double panelRightPx = (base.Left + base.Width) * _sx;
		if ((double)System.Windows.Forms.Cursor.Position.X <= panelRightPx + 160.0)
		{
			_awayTicks = 0;
		}
		else if (++_awayTicks >= 2)
		{
			HideSwitcher();
		}
	}

	private static double ThumbHeight(nint hwnd)
	{
		try
		{
			if (GetWindowRect(hwnd, out var r))
			{
				double w = r.Right - r.Left;
				double h = r.Bottom - r.Top;
				if (w > 0.0 && h > 0.0)
				{
					return Math.Clamp(180.0 * h / w, 132.0, 172.0);
				}
			}
		}
		catch
		{
		}
		return 150.0;
	}

	private static System.Windows.Media.Color SafeAccent()
	{
		try
		{
			return StartAccent.Color();
		}
		catch
		{
			return System.Windows.Media.Color.FromRgb(42, 125, 225);
		}
	}

	private static System.Windows.Media.Color FaintTint(double blend)
	{
		System.Windows.Media.Color a = SafeAccent();
		return System.Windows.Media.Color.FromRgb(Mix(a.R), Mix(a.G), Mix(a.B));
		byte Mix(byte c)
		{
			return (byte)Math.Round((double)(int)c * (1.0 - blend) + 22.0 * blend);
		}
	}

	private void SyncPalette()
	{
		if (SystemParameters.HighContrast)
		{
			_panelBrush = new SolidColorBrush(System.Windows.SystemColors.ControlColor);
			_labelBrush = new SolidColorBrush(System.Windows.SystemColors.ControlColor);
			_thumbBrush = new SolidColorBrush(System.Windows.SystemColors.ControlDarkColor);
			_hoverBrush = new SolidColorBrush(System.Windows.SystemColors.HighlightColor);
			_textBrush = new SolidColorBrush(System.Windows.SystemColors.ControlTextColor);
			base.Background = new SolidColorBrush(System.Windows.SystemColors.WindowColor);
		}
		else
		{
			_panelBrush = new SolidColorBrush(FaintTint(0.88));
			_labelBrush = new SolidColorBrush(FaintTint(0.72));
			_thumbBrush = new SolidColorBrush(FaintTint(0.66));
			_hoverBrush = new SolidColorBrush(FaintTint(0.34));
			_textBrush = new SolidColorBrush(Colors.White);
			base.Background = new LinearGradientBrush(FaintTint(0.82), FaintTint(0.94), 90.0);
		}
		RefreshStartBarPalette();
	}

	private void ConfigureNativeSurface()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		try
		{
			long style = GetWindowLongPtrCompat(_hwnd, GWL_STYLE).ToInt64();
			long framelessStyle = style & ~NATIVE_FRAME_STYLES;
			if (style != framelessStyle)
			{
				SetWindowLongPtrCompat(_hwnd, GWL_STYLE, new IntPtr(framelessStyle));
				SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAME_REFRESH);
			}
			// Win11 can draw a 1-2 px DWM border around an otherwise borderless WPF window. During the
			// switcher's slide-out that border can outlive the client surface for a frame and look like a
			// white line over the foreground app. These attributes affect only this launcher-owned HWND.
			int transitionsDisabled = 1;
			DwmSetWindowAttribute(_hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref transitionsDisabled, sizeof(int));
			int cornerPreference = DWMWCP_DONOTROUND;
			DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
			int borderColor = DWM_COLOR_NONE;
			DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
		}
		catch
		{
			// Older Windows 10 builds can reject the Win11-only color/corner attributes.
		}
	}

	private void ParkNativeSurfaceOffscreen()
	{
		double scaleX = _sx > 0.0 ? _sx : 1.0;
		double scaleY = _sy > 0.0 ? _sy : 1.0;
		try
		{
			if (_hwnd != IntPtr.Zero)
			{
				SetWindowPos(_hwnd, IntPtr.Zero, HIDDEN_NATIVE_COORDINATE, HIDDEN_NATIVE_COORDINATE, 0, 0, SWP_PARK);
			}
		}
		catch
		{
		}
		base.Left = (double)HIDDEN_NATIVE_COORDINATE / scaleX;
		base.Top = (double)HIDDEN_NATIVE_COORDINATE / scaleY;
	}

	private static IntPtr GetWindowLongPtrCompat(nint hwnd, int index)
	{
		return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
	}

	private static void SetWindowLongPtrCompat(nint hwnd, int index, IntPtr value)
	{
		if (IntPtr.Size == 8)
		{
			SetWindowLongPtr64(hwnd, index, value);
		}
		else
		{
			SetWindowLong32(hwnd, index, value.ToInt32());
		}
	}

	private void StyleSwitcherScrollBar()
	{
		if (SystemParameters.HighContrast)
		{
			return;
		}
		try
		{
			Style style = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
			style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 8.0));
			style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty,
				(ControlTemplate)System.Windows.Markup.XamlReader.Parse(SwitcherScrollBarXaml)));
			_root.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
		}
		catch (Exception ex)
		{
			Logger.Log("AppSwitcher scrollbar style failed: " + ex.Message);
		}
	}

	private const string SwitcherScrollBarXaml =
		"<ControlTemplate TargetType=\"ScrollBar\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
		"<Grid Width=\"8\" Background=\"{DynamicResource Metro81.ScrollTrackBrushDark}\" SnapsToDevicePixels=\"True\">" +
		"<Track x:Name=\"PART_Track\" Orientation=\"Vertical\" IsDirectionReversed=\"True\">" +
		"<Track.DecreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageUpCommand\" Focusable=\"False\"><RepeatButton.Template><ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"Transparent\"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>" +
		"<Track.IncreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageDownCommand\" Focusable=\"False\"><RepeatButton.Template><ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"Transparent\"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>" +
		"<Track.Thumb><Thumb MinHeight=\"32\" Margin=\"2,0\"><Thumb.Template><ControlTemplate TargetType=\"Thumb\"><Border x:Name=\"thumb\" Background=\"{DynamicResource Metro81.ScrollThumbBrushDark}\"/><ControlTemplate.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"thumb\" Property=\"Background\" Value=\"{DynamicResource Metro81.ScrollThumbHoverBrushDark}\"/></Trigger><Trigger Property=\"IsDragging\" Value=\"True\"><Setter TargetName=\"thumb\" Property=\"Background\" Value=\"{DynamicResource Metro81.ScrollThumbPressedBrushDark}\"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
		"</Track></Grid></ControlTemplate>";

	private void GoToDesktop()
	{
		foreach (Window w in System.Windows.Application.Current.Windows)
		{
			if (w is StartScreen { IsVisible: not false } ss)
			{
				ss.HideStart();
			}
			else if (w is PcSettingsWindow { IsVisible: not false } pc)
			{
				pc.Close();
			}
		}
		foreach (var box in _boxes)
		{
			nint hwnd = box.Hwnd;
			try
			{
				WindowList.Minimize(hwnd);
			}
			catch
			{
			}
		}
	}

	private static ControlTemplate IconButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 17, 35)), "bd"));
		hover.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.White));
		t.Triggers.Add(hover);
		return t;
	}

	public void QaRender(string outPath)
	{
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		base.Left = -4000.0;
		base.Top = -4000.0;
		base.Height = 900.0;
		Show();
		_hwnd = new WindowInteropHelper(this).Handle;
		BuildEntries();
		UpdateLayout();
		// Render the FULL window content (_slideRoot: search box on top, the scrolling list, and the docked Start bar at
		// the bottom) at the real window height, so the Start button is included and the ScrollViewer clips like it does
		// live. (Rendering just _list omitted the docked chrome.)
		double H = Math.Max(base.ActualHeight, 200.0);
		int hi = (int)Math.Ceiling(H);
		RenderTargetBitmap rtb = new RenderTargetBitmap(200, hi, 96.0, 96.0, PixelFormats.Pbgra32);
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(base.Background, null, new Rect(0.0, 0.0, 200.0, (double)hi));
		}
		rtb.Render(dv);
		rtb.Render(_slideRoot);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using (FileStream fs = File.Create(outPath))
		{
			enc.Save(fs);
		}
		Hide();
		ParkNativeSurfaceOffscreen();
		Logger.Log("AppSwitcher QaRender -> " + outPath);
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		e.Cancel = true;
		HideSwitcher();
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUnregisterThumbnail(nint thumb);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_THUMBNAIL_PROPERTIES props);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong32(nint hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern IntPtr GetWindowLongPtr64(nint hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
	private static extern int SetWindowLong32(nint hwnd, int index, int value);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static extern IntPtr SetWindowLongPtr64(nint hwnd, int index, IntPtr value);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hwnd, out RECT r);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
