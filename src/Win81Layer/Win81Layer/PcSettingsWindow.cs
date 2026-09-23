using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Win81Layer;

public sealed class PcSettingsWindow : Window
{
	private delegate void WinEventProc(nint hHook, uint ev, nint hwnd, int idObj, int idChild, uint thread, uint time);

	private sealed class PageHost : Border
	{
		protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
		{
			base.OnVisualChildrenChanged(visualAdded, visualRemoved);
			if (visualAdded is UIElement el)
			{
				TranslateTransform slide = (TranslateTransform)(el.RenderTransform = new TranslateTransform(0.0, 16.0));
				el.Opacity = 0.0;
				IEasingFunction ease = Motion.Ease(Motion.Cat.Navigation);
				Duration d = Motion.Dur(Motion.Cat.Navigation);
				slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16.0, 0.0, d)
				{
					EasingFunction = ease
				});
				el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, d)
				{
					EasingFunction = ease
				});
			}
		}
	}

	private readonly StartScreen _start;

	private readonly PageHost _content;

	private readonly Dictionary<string, System.Windows.Controls.Button> _navButtons = new Dictionary<string, System.Windows.Controls.Button>();

	private string _current = "";

	private TextBlock _headerLabel = null;

	private StackPanel _navPanel = null;

	private Action? _backAction;

	private readonly Dictionary<string, System.Windows.Controls.Button> _eoaButtons = new Dictionary<string, System.Windows.Controls.Button>();

	private string _eoaCurrent = "";

	private readonly Dictionary<string, System.Windows.Controls.Button> _netButtons = new Dictionary<string, System.Windows.Controls.Button>();

	private string _netCurrent = "";

	private Border _leftPaneBorder = null;

	private const double TitleH = 32.0;

	private Border _titleBar = null;

	private TranslateTransform _titleSlide = null;

	private bool _titleShown;

	private static readonly System.Windows.Media.Brush RightBg = System.Windows.Media.Brushes.White;

	private static readonly System.Windows.Media.Brush Text = Freeze(4281545523u);

	private static readonly System.Windows.Media.Brush Text2 = Freeze(4286216826u);

	private static readonly (string Name, string? Uri)[] Categories = new(string, string)[14]
	{
		("PC & devices", null),
		("Launcher", null),
		("Desktop composition", null),
		("Accounts", "ms-settings:yourinfo"),
		("OneDrive", "ms-settings:"),
		("Search and apps", "ms-settings:cortana"),
		("Privacy", "ms-settings:privacy"),
		("Network", "ms-settings:network-status"),
		("Time and language", "ms-settings:regionlanguage"),
		("Ease of Access", "ms-settings:easeofaccess"),
		("Update and recovery", "ms-settings:windowsupdate"),
		("Control Panel", null),
		("Performance tools", null),
		("Experiments", null)
	};

	private static readonly string[] Palette = new string[20]
	{
		"#512BD4", "#6A2C91", "#8E2DC0", "#A61E5A", "#C81E5B", "#E3008C", "#E81123", "#EF6950", "#FF8C00", "#C19C00",
		"#10893E", "#00B294", "#00B7C3", "#0091F7", "#2672EC", "#4617B4", "#1F1F1F", "#5A3E85", "#B146C2", "#767676"
	};

	// The authentic Windows 8.1 desktop accent palette (Control Panel → Personalization → Color, "Change the
	// colour of your window borders and taskbar"), captured from the reference VM. Distinct from the Metro tile
	// palette. An empty StartAccentColor selects "Automatic" (derived from the wallpaper), exactly like 8.1.
	private static readonly string[] AuthenticAccents = new string[15]
	{
		"#8C8C8C", "#86C5E8", "#EAA3CB", "#E8CB4C", "#A6C64F", "#7FD0C9", "#EFA24C",
		"#F17E75", "#EA55A0", "#45C06A", "#C39BE8", "#5DB0EF", "#9296EC", "#BBAE9E", "#F5F5F5"
	};

	private static ImageSource? _gearImg;

	private WinEventProc? _foregroundProc;

	private nint _foregroundHook;

	private nint _hwnd;

	private static readonly string[] EoaItems = new string[6] { "Narrator", "Magnifier", "High contrast", "Keyboard", "Mouse", "Other options" };

	private static readonly string[] NetItems = new string[5] { "Connections", "Airplane mode", "Proxy", "HomeGroup", "Workplace" };

	private static readonly (string Label, int Min)[] TimeoutOptions = new(string, int)[16]
	{
		("1 minute", 1),
		("2 minutes", 2),
		("3 minutes", 3),
		("5 minutes", 5),
		("10 minutes", 10),
		("15 minutes", 15),
		("20 minutes", 20),
		("25 minutes", 25),
		("30 minutes", 30),
		("45 minutes", 45),
		("1 hour", 60),
		("2 hours", 120),
		("3 hours", 180),
		("4 hours", 240),
		("5 hours", 300),
		("Never", 0)
	};

	private static System.Windows.Media.Brush LeftBg
	{
		get
		{
			if (ShellSkin.GlassOn)
			{
				// Win7-Aero identity on an opaque full-screen window: a lighter accent rail with a top-down gradient
				// sheen (NOT see-through — this window never gets AllowsTransparency).
				System.Windows.Media.Color top = SettingsPane.AccentToneColor(0.35);
				System.Windows.Media.Color bot = SettingsPane.AccentToneColor(0.26);
				return new System.Windows.Media.LinearGradientBrush(top, bot, 90.0);
			}
			return new SolidColorBrush(SettingsPane.AccentToneColor(0.32));
		}
	}

	private static System.Windows.Media.Brush TitleAccent => new SolidColorBrush(SettingsPane.AccentToneColor(0.46));

	internal static nint LiveHwnd { get; private set; }

	public event Action? SearchRequested;

	private static System.Windows.Media.Brush Freeze(uint argb)
	{
		SolidColorBrush b = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
		((Freezable)b).Freeze();
		return b;
	}

	public PcSettingsWindow(StartScreen start)
	{
		if (!ShellSkin.GlassOn) MetroPatternTheme.Apply(this);
		_start = start;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = true;
		base.Background = RightBg;
		base.Title = "PC settings";
		base.PreviewKeyDown += delegate(object _, System.Windows.Input.KeyEventArgs e)
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Invalid comparison between Unknown and I4
			if ((int)e.Key == 13)
			{
				Close();
			}
		};
		Grid grid = new Grid
		{
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = new GridLength(320.0)
				},
				new ColumnDefinition()
			},
			Children = { (UIElement)BuildLeftPane() }
		};
		_content = new PageHost
		{
			Background = RightBg
		};
		Grid.SetColumn(_content, 1);
		grid.Children.Add(_content);
		base.Content = new Grid
		{
			Children = 
			{
				(UIElement)grid,
				(UIElement)BuildTitleBar()
			}
		};
		base.PreviewMouseMove += OnTitleReveal;
		try
		{
			base.Icon = BuildGearIcon();
		}
		catch
		{
		}
	}

	private FrameworkElement BuildTitleBar()
	{
		_titleSlide = new TranslateTransform(0.0, -32.0);
		_titleBar = new Border
		{
			Height = 32.0,
			Background = Freeze(4278190080u),
			VerticalAlignment = VerticalAlignment.Top,
			RenderTransform = _titleSlide
		};
		System.Windows.Controls.Panel.SetZIndex(_titleBar, 100);
		DockPanel dock = new DockPanel
		{
			LastChildFill = false
		};
		System.Windows.Media.Color accent;
		try
		{
			accent = StartAccent.Color();
		}
		catch
		{
			accent = System.Windows.Media.Color.FromRgb(81, 43, 212);
		}
		System.Windows.Controls.Image titleGear = new System.Windows.Controls.Image
		{
			Source = WhiteGear(),
			Stretch = Stretch.Uniform,
			Width = 16.0,
			Height = 16.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		RenderOptions.SetBitmapScalingMode((DependencyObject)(object)titleGear, BitmapScalingMode.HighQuality);
		Border iconSquare = new Border
		{
			Width = 18.0,
			Height = 18.0,
			Background = new SolidColorBrush(accent),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			Child = titleGear
		};
		DockPanel.SetDock(iconSquare, Dock.Left);
		dock.Children.Add(iconSquare);
		Border close = MakeCaptionButton("\ue8bb", isClose: true, base.Close);
		Border min = MakeCaptionButton("\ue921", isClose: false, delegate
		{
			base.WindowState = WindowState.Minimized;
		});
		DockPanel.SetDock(close, Dock.Right);
		DockPanel.SetDock(min, Dock.Right);
		dock.Children.Add(close);
		dock.Children.Add(min);
		TextBlock title = new TextBlock
		{
			Text = "PC settings",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semilight"),
			FontSize = 12.5,
			Foreground = System.Windows.Media.Brushes.White,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			IsHitTestVisible = false
		};
		Grid inner = new Grid();
		inner.Children.Add(dock);
		inner.Children.Add(title);
		_titleBar.Child = inner;
		return _titleBar;
	}

	private Border MakeCaptionButton(string glyph, bool isClose, Action onClick)
	{
		Border host = new Border
		{
			Width = 46.0,
			Height = 32.0,
			Background = System.Windows.Media.Brushes.Transparent,
			Cursor = System.Windows.Input.Cursors.Hand
		};
		host.Child = new TextBlock
		{
			Text = glyph,
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 10.0,
			Foreground = System.Windows.Media.Brushes.White,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		System.Windows.Media.Brush hover = (isClose ? Freeze(4293398819u) : Freeze(4280953386u));
		host.MouseEnter += delegate
		{
			host.Background = hover;
		};
		host.MouseLeave += delegate
		{
			host.Background = System.Windows.Media.Brushes.Transparent;
		};
		host.MouseLeftButtonUp += delegate
		{
			onClick();
		};
		return host;
	}

	private void OnTitleReveal(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		System.Windows.Point position = e.GetPosition(this);
		double y = position.Y;
		if (y <= 4.0)
		{
			ShowTitleBar();
		}
		else if (y > 50.0 && !_titleBar.IsMouseOver)
		{
			HideTitleBar();
		}
	}

	private void ShowTitleBar()
	{
		if (!_titleShown)
		{
			_titleShown = true;
			_titleSlide.BeginAnimation(TranslateTransform.YProperty, Motion.To(0.0, Motion.Cat.EdgeEnter));
		}
	}

	private void HideTitleBar()
	{
		if (_titleShown)
		{
			_titleShown = false;
			_titleSlide.BeginAnimation(TranslateTransform.YProperty, Motion.To(-32.0, Motion.Cat.EdgeExit));
		}
	}

	private FrameworkElement BuildLeftPane()
	{
		Border root = (_leftPaneBorder = new Border
		{
			Background = LeftBg
		});
		DockPanel dock = new DockPanel();
		Border backRow = new Border
		{
			Margin = new Thickness(36.0, 38.0, 24.0, 6.0)
		};
		DockPanel.SetDock(backRow, Dock.Top);
		MetroDirectionalArrow81 back = new MetroDirectionalArrow81
		{
			Width = 44.0,
			Height = 44.0,
			GlyphSize = 32.0,
			Direction = MetroArrowDirection81.Left,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			ToolTip = "Back — close PC settings"
		};
		System.Windows.Automation.AutomationProperties.SetName(back, "Back — close PC settings");
		back.Click += delegate
		{
			if (_backAction != null)
			{
				_backAction();
			}
			else
			{
				Close();
			}
		};
		backRow.Child = back;
		dock.Children.Add(backRow);
		Grid header = new Grid
		{
			Margin = new Thickness(40.0, 4.0, 24.0, 24.0)
		};
		header.ColumnDefinitions.Add(new ColumnDefinition());
		header.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		_headerLabel = new TextBlock
		{
			Text = "PC settings",
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 30.0,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		header.Children.Add(_headerLabel);
		System.Windows.Controls.Button search = new System.Windows.Controls.Button
		{
			Content = new TextBlock
			{
				Text = '\ue11a'.ToString(),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"),
				FontSize = 16.0,
				Foreground = System.Windows.Media.Brushes.White,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			},
			Width = 34.0,
			Height = 30.0,
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			ToolTip = "Search",
			Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(0.0),
			Template = FlatButtonTemplate(),
			VerticalAlignment = VerticalAlignment.Center
		};
		search.Click += delegate
		{
			SearchRequested?.Invoke();
		};
		if (!ShellSkin.GlassOn)
		{
			search.ClearValue(System.Windows.Controls.Control.TemplateProperty);
			search.ClearValue(System.Windows.Controls.Control.FocusVisualStyleProperty);
			search.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.CommandButton");
		}
		Grid.SetColumn(search, 1);
		header.Children.Add(search);
		DockPanel.SetDock(header, Dock.Top);
		dock.Children.Add(header);
		System.Windows.Controls.Button cp = NavButton("Control Panel");
		cp.Click += delegate
		{
			Launch("control");
		};
		cp.FontSize = 15.0;
		cp.Margin = new Thickness(0.0, 0.0, 0.0, 20.0);
		DockPanel.SetDock(cp, Dock.Bottom);
		dock.Children.Add(cp);
		_navPanel = new StackPanel
		{
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		PopulateCategories();
		ScrollViewer scroller = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = _navPanel
		};
		dock.Children.Add(scroller);
		root.Child = dock;
		return root;
	}

	private void PopulateCategories()
	{
		_navPanel.Children.Clear();
		_navButtons.Clear();
		(string, string)[] categories = Categories;
		for (int i = 0; i < categories.Length; i++)
		{
			string name = categories[i].Item1;
			System.Windows.Controls.Button b = NavButton(name);
			_navButtons[name] = b;
			_navPanel.Children.Add(b);
		}
	}

	private System.Windows.Controls.Button NavButtonBare(string name)
	{
		if (!ShellSkin.GlassOn)
		{
			var nav = new System.Windows.Controls.Button { Content = name };
			nav.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.NavButton");
			return nav;
		}
		return new System.Windows.Controls.Button
		{
			Content = name,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 19.0,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Padding = new Thickness(40.0, 11.0, 20.0, 11.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Template = NavTemplate()
		};
	}

	private System.Windows.Controls.Button NavButton(string name)
	{
		System.Windows.Controls.Button b = NavButtonBare(name);
		b.Click += delegate
		{
			Nav(name);
		};
		return b;
	}

	public void ShowFullScreen()
	{
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0198: Unknown result type (might be due to invalid IL or missing references)
		_leftPaneBorder.Background = LeftBg;
		Show();
		Screen screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		System.Drawing.Rectangle wa = TaskbarWorkArea.Current(screen);
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
		double sx = num ?? 1.0;
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
		double sy = num2 ?? 1.0;
		if (sx <= 0.0)
		{
			sx = 1.0;
		}
		if (sy <= 0.0)
		{
			sy = 1.0;
		}
		base.Left = (double)wa.Left / sx;
		base.Top = (double)wa.Top / sy;
		base.Width = (double)wa.Width / sx;
		base.Height = (double)wa.Height / sy;
		Activate();
		Nav((_current.Length == 0) ? "PC & devices" : _current);
		TaskbarWindow.RaiseTaskbarLayoutChanged();
		Grid root = (Grid)base.Content;
		root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		root.Opacity = 1.0;
		if (Motion.Mode == MotionMode.Off)
		{
			root.RenderTransform = null;
			return;
		}
		ScaleTransform scale = (ScaleTransform)(root.RenderTransform = new ScaleTransform(0.99, 0.99));
		IEasingFunction ez = Motion.Ease(Motion.Cat.Enter);
		Duration d = Motion.Dur(Motion.Cat.Enter);
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.99, 1.0, d)
		{
			EasingFunction = ez
		});
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.99, 1.0, d)
		{
			EasingFunction = ez
		});
		PlayOpenSplash();
	}

	private void PlayOpenSplash()
	{
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		System.Windows.Media.Color accent;
		try
		{
			accent = StartAccent.Color();
		}
		catch
		{
			accent = System.Windows.Media.Color.FromRgb(106, 44, 145);
		}
		Border splash = new Border
		{
			Background = new SolidColorBrush(accent)
		};
		ScaleTransform st = new ScaleTransform(0.86, 0.86);
		System.Windows.Controls.Image splashGear = new System.Windows.Controls.Image
		{
			Source = WhiteGear(),
			Stretch = Stretch.Uniform,
			Width = 160.0,
			Height = 160.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			RenderTransform = st
		};
		RenderOptions.SetBitmapScalingMode((DependencyObject)(object)splashGear, BitmapScalingMode.HighQuality);
		splash.Child = splashGear;
		System.Windows.Controls.Panel.SetZIndex(splash, 200);
		Grid root = (Grid)base.Content;
		root.Children.Add(splash);
		Duration d = Motion.Dur(Motion.Cat.Enter);
		IEasingFunction ez = Motion.Ease(Motion.Cat.Enter);
		st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1.0, d)
		{
			EasingFunction = ez
		});
		st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1.0, d)
		{
			EasingFunction = ez
		});
		DoubleAnimation fade = new DoubleAnimation(1.0, 0.0, Motion.Dur(Motion.Cat.Exit))
		{
			BeginTime = TimeSpan.FromMilliseconds(300L)
		};
		fade.Completed += delegate
		{
			root.Children.Remove(splash);
		};
		splash.BeginAnimation(UIElement.OpacityProperty, fade);
	}

	internal static ImageSource WhiteGear()
	{
		// Unified with the launcher's ONE canonical Settings cog (SettingsGlyph): a crisp vector gear rather than a
		// 256px raster that gets downscaled, so PC Settings matches the Action Center / menus and stays sharp at
		// every size. (SettingsGlyph caches internally.)
		return SettingsGlyph.Gear();
	}

	internal static ImageSource BuildGearIcon()
	{
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		System.Windows.Media.Color accent;
		try
		{
			accent = StartAccent.Color();
		}
		catch
		{
			accent = System.Windows.Media.Color.FromRgb(81, 43, 212);
		}
		System.Windows.Controls.Image img = new System.Windows.Controls.Image
		{
			Source = WhiteGear(),
			Stretch = Stretch.Uniform,
			Width = 46.0,
			Height = 46.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		RenderOptions.SetBitmapScalingMode((DependencyObject)(object)img, BitmapScalingMode.HighQuality);
		Border host = new Border
		{
			Width = 64.0,
			Height = 64.0,
			Background = new SolidColorBrush(accent),
			Child = img
		};
		host.Measure(new System.Windows.Size(64.0, 64.0));
		host.Arrange(new Rect(0.0, 0.0, 64.0, 64.0));
		host.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap(64, 64, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(host);
		((Freezable)rtb).Freeze();
		return rtb;
	}

	internal static ImageSource BuildGearGlyph(int size = 48)
	{
		return WhiteGear();
	}

	private void WatchForDesktop()
	{
		if (_foregroundHook == IntPtr.Zero)
		{
			_foregroundProc = OnForegroundChanged;
			_foregroundHook = SetWinEventHook(3u, 3u, IntPtr.Zero, _foregroundProc, 0u, 0u, 0u);
		}
	}

	private void OnForegroundChanged(nint hHook, uint ev, nint hwnd, int idObj, int idChild, uint thread, uint time)
	{
		if (idObj != 0 || hwnd == IntPtr.Zero)
		{
			return;
		}
		StringBuilder sb = new StringBuilder(64);
		GetClassName(hwnd, sb, sb.Capacity);
		string cls = sb.ToString();
		if ((!(cls == "Progman") && !(cls == "WorkerW")) || 1 == 0)
		{
			return;
		}
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			if (base.IsVisible)
			{
				Close();
			}
		}, Array.Empty<object>());
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_hwnd = new WindowInteropHelper(this).Handle;
		LiveHwnd = _hwnd;
	}

	protected override void OnClosed(EventArgs e)
	{
		if (LiveHwnd == _hwnd)
		{
			LiveHwnd = IntPtr.Zero;
		}
		TaskbarWindow.RaiseTaskbarLayoutChanged();
		if (_foregroundHook != IntPtr.Zero)
		{
			UnhookWinEvent(_foregroundHook);
			_foregroundHook = IntPtr.Zero;
		}
		_foregroundProc = null;
		base.OnClosed(e);
	}

	[DllImport("user32.dll")]
	private static extern nint SetWinEventHook(uint min, uint max, nint hmod, WinEventProc proc, uint pid, uint tid, uint flags);

	[DllImport("user32.dll")]
	private static extern bool UnhookWinEvent(nint h);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint h, StringBuilder sb, int max);

	private void Nav(string name)
	{
		if (name == "Ease of Access")
		{
			DrillIntoEoa();
			return;
		}
		if (name == "Network")
		{
			DrillIntoNetwork();
			return;
		}
		if (_backAction != null)
		{
			_headerLabel.Text = "PC settings";
			_headerLabel.FontSize = 30.0;
			PopulateCategories();
			_backAction = null;
		}
		_current = name;
		foreach (var (cat, btn) in _navButtons)
		{
			btn.Tag = ((cat == name) ? "sel" : null);
		}
		_content.Child = BuildPage(name);
	}

	private void DrillIntoEoa()
	{
		_headerLabel.Text = "Ease of Access";
		_headerLabel.FontSize = 24.0;
		_navPanel.Children.Clear();
		_eoaButtons.Clear();
		string[] eoaItems = EoaItems;
		foreach (string item in eoaItems)
		{
			System.Windows.Controls.Button b = NavButtonBare(item);
			string sub = item;
			b.Click += delegate
			{
				EoaNav(sub);
			};
			_eoaButtons[item] = b;
			_navPanel.Children.Add(b);
		}
		_backAction = UnDrill;
		EoaNav("Narrator");
	}

	private void UnDrill()
	{
		Nav((_current.Length == 0) ? "PC & devices" : _current);
	}

	private void EoaNav(string sub)
	{
		_eoaCurrent = sub;
		foreach (var (name, btn) in _eoaButtons)
		{
			btn.Tag = ((name == sub) ? "sel" : null);
		}
		_content.Child = EoaPage(sub);
	}

	private UIElement EoaPage(string sub)
	{
		if (1 == 0)
		{
		}
		UIElement result = sub switch
		{
			"Narrator" => EoaTogglePage("Narrator", "Hear text and controls on the screen read aloud.", IsRunning("Narrator"), delegate(bool on)
			{
				ToggleProcess("narrator.exe", "Narrator", on);
			}), 
			"Magnifier" => EoaTogglePage("Magnifier", "Make everything on the screen bigger.", IsRunning("Magnify"), delegate(bool on)
			{
				ToggleProcess("magnify.exe", "Magnify", on);
			}), 
			"High contrast" => EoaRoutePage("High contrast", "Choose a high-contrast theme to make the screen easier to see.", "ms-settings:easeofaccess-highcontrast"), 
			"Keyboard" => EoaKeyboardPage(), 
			"Mouse" => EoaRoutePage("Mouse", "Change the pointer size and use the number pad to move the mouse.", "ms-settings:easeofaccess-mousepointer"), 
			"Other options" => EoaRoutePage("Other options", "Animations, background, and how long notifications stay on screen.", "ms-settings:easeofaccess-visualeffects"), 
			_ => EoaRoutePage(sub, "", "ms-settings:easeofaccess"), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private UIElement EoaTogglePage(string title, string desc, bool on, Action<bool> set)
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle(title));
		panel.Children.Add(new TextBlock
		{
			Text = desc,
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Margin = new Thickness(0.0, 4.0, 0.0, 12.0),
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 560.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		});
		FrameworkElement toggle = Toggle(on, set);
		System.Windows.Automation.AutomationProperties.SetName(toggle, title);
		panel.Children.Add(toggle);
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement EoaKeyboardPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("On-Screen Keyboard"));
		panel.Children.Add(ToggleRow("On-Screen Keyboard", "Use your device without a physical keyboard.", IsRunning("osk"), delegate(bool on)
		{
			ToggleProcess("osk.exe", "osk", on);
		}));
		panel.Children.Add(new Border
		{
			Height = 12.0
		});
		panel.Children.Add(SectionTitle("Useful keys"));
		panel.Children.Add(ToggleRow("Sticky Keys", "Press one key at a time for keyboard shortcuts.", AccessibilityKeys.StickyOn(), AccessibilityKeys.SetSticky));
		panel.Children.Add(ToggleRow("Toggle Keys", "Hear a tone when you press Caps Lock, Num Lock and Scroll Lock.", AccessibilityKeys.ToggleOn(), AccessibilityKeys.SetToggle));
		panel.Children.Add(ToggleRow("Filter Keys", "Ignore brief or repeated keystrokes.", AccessibilityKeys.FilterOn(), AccessibilityKeys.SetFilter));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement EoaRoutePage(string title, string desc, string uri)
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle(title));
		if (!string.IsNullOrEmpty(desc))
		{
			panel.Children.Add(new TextBlock
			{
				Text = desc,
				Foreground = Text2,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 13.0,
				Margin = new Thickness(0.0, 4.0, 0.0, 14.0),
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 560.0,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Left
			});
		}
		panel.Children.Add(ActionButton("Open " + title + " settings", delegate
		{
			Launch(uri);
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement BuildPage(string name)
	{
		string uri = Array.Find(Categories, ((string Name, string Uri) c) => c.Name == name).Uri;
		if (1 == 0)
		{
		}
		UIElement result = name switch
		{
			"PC & devices" => PcAndDevicesPage(),
			"Launcher" => LauncherPage(),
			"Desktop composition" => CompositionPage(),
			"Accounts" => AccountsPage(),
			"Network" => NetworkPage(), 
			"Search and apps" => SearchAndAppsPage(), 
			"Time and language" => TimeLanguagePage(), 
			"Update and recovery" => UpdateRecoveryPage(),
			"Ease of Access" => EaseOfAccessPage(),
			"Control Panel" => ControlPanelPage(),
			"Performance tools" => PerformanceToolsPage(),
			"Experiments" => ExperimentsPage(),
			_ => RoutePage(name, uri),
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private string _compSel = "";

	private string _shellProfileSel = "";

	private StackPanel _shellProfileTiles;

	private TextBlock _shellProfileStatus;

	private StackPanel _compTiles;

	private StackPanel _compStatus;

	private string _compStatusSignature = "";

	private System.Windows.Threading.DispatcherTimer _compStatusTimer;

	// The reference "Desktop Composition" page: 4 mode tiles + options + colour accent + quick actions + live status.
	private UIElement CompositionPage()
	{
		_compSel = CompositionProfiles.Resolve(DesktopComposition.Mode).Id;
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Desktop composition"));
		panel.Children.Add(new TextBlock
		{
			Text = "Choose the visual style and behaviour for windows, effects and desktop composition. Uses the modern Windows DWM as the backend — no system files are replaced. Fully reversible.",
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 760.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 2.0, 0.0, 16.0)
		});

		_compTiles = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		panel.Children.Add(_compTiles);
		RebuildCompTiles();

		StackPanel actions = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 16.0, 0.0, 6.0)
		};
		actions.Children.Add(CompActionBtn("Apply", primary: true, delegate
		{
			DesktopComposition.SetMode(_compSel);
			AfterCompChange();
		}));
		actions.Children.Add(CompActionBtn("Preview (10s)", primary: false, delegate
		{
			DesktopComposition.PreviewMode(_compSel, 10);
			AfterCompChange();
		}));
		actions.Children.Add(CompActionBtn("Restore previous", primary: false, delegate
		{
			DesktopComposition.RestorePrevious();
			_compSel = CompositionProfiles.Resolve(DesktopComposition.Mode).Id;
			AfterCompChange();
		}));
		actions.Children.Add(CompActionBtn("Restore Windows default", primary: false, delegate
		{
			DesktopComposition.RestoreWindowsDefault();
			_compSel = "native";
			AfterCompChange();
		}));
		actions.Children.Add(CompActionBtn("Reset", primary: false, delegate
		{
			DesktopComposition.Reset();
			_compSel = "native";
			AfterCompChange();
		}));
		panel.Children.Add(actions);

		panel.Children.Add(SubHeader("Options"));
		AppSettings s = SettingsStore.Load();
		panel.Children.Add(ToggleRow("Transparency effects", "Translucent glass tints on Alchemy's own surfaces.", s.DeskCompTransparency, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.DeskCompTransparency = v;
			});
			ReskinShell();
			RefreshCompStatus();
		}));
		panel.Children.Add(ToggleRow("Window shadows", "Drop shadow on Alchemy's own windows (via the DWM frame).", s.DeskCompShadows, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.DeskCompShadows = v;
			});
			Win81Window.RefreshAllShadows();
			RefreshCompStatus();
		}));
		panel.Children.Add(ToggleRow("Composition animations", "Alchemy's own window and UI animations.", s.DeskCompAnimations, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.DeskCompAnimations = v;
			});
			Motion.Mode = (v ? MotionMode.Authentic : MotionMode.Off);
			RefreshCompStatus();
		}));
		panel.Children.Add(ToggleRow("Optimize for performance", "Reduce animations and heavy effects.", s.DeskCompOptimizePerf, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.DeskCompOptimizePerf = v;
			});
			if (v)
			{
				Motion.Mode = MotionMode.Reduced;
			}
			RefreshCompStatus();
		}));

		panel.Children.Add(CompSlider("Shell tint opacity (blur intensity)", s.DeskCompBlur, delegate(int v)
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.DeskCompBlur = v;
			});
			ReskinShell();
		}));

		panel.Children.Add(ToggleRow("8.1 window caption: authentic light", "Owned 8.1 windows use the authentic WHITE caption (dark glyphs, red-at-rest close) — the VM-verified out-of-box 8.1 default. Off = accent-coloured caption (the 'show color on title bar' variant). Only affects the flat 8.1 frame style, not Win7-Aero.", s.Win81LightCaption, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.Win81LightCaption = v; });
			Win81Window.RefreshAllAccents();
		}));

		// DWMBlurGlass removed (buggy) — the Win7-Aero toggle is gone; Win7 uses the launcher's own owned-frame Aero.

		panel.Children.Add(SubHeader("Diagnostics (advanced)"));
		panel.Children.Add(ToggleRow("DWM frame-timing telemetry", "Enable on-demand frame-timing capture. When on, the tray 'Capture DWM frame timing' item or the button below writes a bounded ~5s report to %LocalAppData%\\Win81Layer\\dwm-diag. It samples from the live shell (real composition context) and never runs a background loop. Off = no capture.", s.EnableDwmTimingTelemetry, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.EnableDwmTimingTelemetry = v;
			});
		}));
		panel.Children.Add(CompActionBtn("Capture frame timing (5s)", primary: false, delegate
		{
			if (!SettingsStore.Load().EnableDwmTimingTelemetry)
			{
				return;
			}
			nint h = HostApp?.ShellHwndForDiagnostics() ?? 0;
			System.Threading.Tasks.Task.Run((Action)delegate
			{
				try
				{
					DwmDiagnostics.FrameTimingBurst(h, 300, 16);
				}
				catch
				{
				}
			});
		}));

		panel.Children.Add(SubHeader("Colour accent"));
		panel.Children.Add(CompAccentSwatches());

		panel.Children.Add(SubHeader("Current status"));
		_compStatus = new StackPanel
		{
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		_compStatusSignature = "";
		panel.Children.Add(_compStatus);
		RefreshCompStatus();
		StartCompStatusTimer();
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Content = panel
		};
	}

	private static TextBlock SubHeader(string text)
	{
		return new TextBlock
		{
			Text = text,
			Foreground = TitleAccent,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semilight"),
			FontSize = 18.0,
			Margin = new Thickness(0.0, 20.0, 0.0, 6.0)
		};
	}

	private void RebuildCompTiles()
	{
		if (_compTiles == null)
		{
			return;
		}
		_compTiles.Children.Clear();
		int[] glyphs = new int[4] { 0xE770, 0xE771, 0xE80F, 0xE7F4 };   // MDL2: keyboard-ish / arrow / globe / diagnostic (placeholders)
		int gi = 0;
		foreach (var (Label, Id) in CompositionProfiles.MenuModes)
		{
			bool sel = string.Equals(Id, _compSel, StringComparison.OrdinalIgnoreCase);
			string id = Id;
			Border tile = CompModeTile(Label, glyphs[gi % 4], sel, delegate
			{
				_compSel = id;
				RebuildCompTiles();
			});
			_compTiles.Children.Add(tile);
			gi++;
		}
	}

	private Border CompModeTile(string label, int glyph, bool selected, Action onClick)
	{
		Grid g = new Grid
		{
			Width = 176.0,
			Height = 116.0,
			Cursor = System.Windows.Input.Cursors.Hand,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(selected ? 0.46 : 0.32))
		};
		g.Children.Add(new TextBlock
		{
			Text = ((char)glyph).ToString(),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 34.0,
			Foreground = System.Windows.Media.Brushes.White,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 0.0, 22.0)
		});
		Border cap = new Border
		{
			Height = 30.0,
			VerticalAlignment = VerticalAlignment.Bottom,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.5))
		};
		cap.Child = new TextBlock
		{
			Text = label,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		};
		g.Children.Add(cap);
		Border tile = new Border
		{
			Child = g,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
			BorderThickness = new Thickness(selected ? 2.0 : 0.0),
			BorderBrush = System.Windows.Media.Brushes.White
		};
		tile.MouseLeftButtonUp += delegate
		{
			onClick();
		};
		return tile;
	}

	private Border CompActionBtn(string label, bool primary, Action onClick)
	{
		Border b = new Border
		{
			Height = 34.0,
			MinWidth = 96.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			CornerRadius = new CornerRadius(2.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Background = new SolidColorBrush(primary ? SettingsPane.AccentToneColor(0.5) : SettingsPane.AccentToneColor(0.28))
		};
		b.Child = new TextBlock
		{
			Text = label,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			Margin = new Thickness(12.0, 0.0, 12.0, 0.0)
		};
		b.MouseLeftButtonUp += delegate
		{
			onClick();
		};
		return b;
	}

	private FrameworkElement CompSlider(string label, int value, Action<int> onChange)
	{
		StackPanel sp = new StackPanel
		{
			Margin = new Thickness(0.0, 10.0, 0.0, 2.0)
		};
		sp.Children.Add(new TextBlock
		{
			Text = label,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0
		});
		Slider sl = new Slider
		{
			Minimum = 0.0,
			Maximum = 100.0,
			Value = value,
			Width = 360.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
			IsSnapToTickEnabled = true,
			TickFrequency = 1.0
		};
		sl.ValueChanged += delegate
		{
			onChange((int)sl.Value);
		};
		sp.Children.Add(sl);
		return sp;
	}

	private FrameworkElement CompAccentSwatches()
	{
		WrapPanel wp = new WrapPanel
		{
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		};
		System.Collections.Generic.List<(Border ring, string key, TextBlock chk)> rings = new System.Collections.Generic.List<(Border, string, TextBlock)>();
		// Automatic (rainbow, key="") first, then the authentic Windows 8.1 accent swatches.
		System.Collections.Generic.List<(string key, System.Windows.Media.Brush fill, string tip, System.Windows.Media.Color ink)> entries = new System.Collections.Generic.List<(string, System.Windows.Media.Brush, string, System.Windows.Media.Color)>();
		LinearGradientBrush auto = new LinearGradientBrush
		{
			StartPoint = new System.Windows.Point(0.0, 0.0),
			EndPoint = new System.Windows.Point(1.0, 1.0)
		};
		auto.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(232, 17, 35), 0.0));
		auto.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(232, 203, 76), 0.25));
		auto.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(69, 192, 106), 0.5));
		auto.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(93, 176, 239), 0.75));
		auto.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(146, 150, 236), 1.0));
		((Freezable)auto).Freeze();
		entries.Add(("", auto, "Automatic (from wallpaper)", System.Windows.Media.Colors.White));
		foreach (string hex in AuthenticAccents)
		{
			System.Windows.Media.Color c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
			entries.Add((hex, new SolidColorBrush(c), hex, ColorMath.ReadableInk(c)));
		}
		System.Action refresh = delegate
		{
			string cur = (SettingsStore.Load().StartAccentColor ?? "").Trim();
			foreach ((Border ring, string key, TextBlock chk) r in rings)
			{
				bool sel = string.Equals(r.key, cur, System.StringComparison.OrdinalIgnoreCase);
				r.ring.BorderBrush = (sel ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Transparent);
				r.chk.Visibility = (sel ? Visibility.Visible : Visibility.Collapsed);
			}
		};
		foreach ((string key, System.Windows.Media.Brush fill, string tip, System.Windows.Media.Color ink) e in entries)
		{
			string key = e.key;
			TextBlock chk = new TextBlock
			{
				Text = "✓",
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"),
				FontSize = 14.0,
				Foreground = new SolidColorBrush(e.ink),
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Visibility = Visibility.Collapsed
			};
			Border sw = new Border
			{
				Width = 30.0,
				Height = 30.0,
				Cursor = System.Windows.Input.Cursors.Hand,
				Background = e.fill,
				ToolTip = e.tip,
				Child = chk
			};
			Border ring = new Border
			{
				BorderThickness = new Thickness(2.0),
				BorderBrush = System.Windows.Media.Brushes.Transparent,
				Margin = new Thickness(0.0, 0.0, 6.0, 6.0),
				Child = sw
			};
			sw.MouseLeftButtonUp += delegate
			{
				SettingsStore.Update(delegate(AppSettings x)
				{
					x.StartAccentColor = key;
				});
				try
				{
					DesktopComposition.RefreshAccent();
				}
				catch
				{
				}
				ReskinShell();
				RebuildCompTiles();
				RefreshCompStatus();
				refresh();
			};
			rings.Add((ring, key, chk));
			wp.Children.Add(ring);
		}
		refresh();
		return wp;
	}

	private void ReskinShell()
	{
		try
		{
			TaskbarWindow.RaiseTaskbarColorChanged(invalidateWallpaper: false);
			Win81Window.RefreshAllAccents();
		}
		catch
		{
		}
	}

	private void AfterCompChange()
	{
		RebuildCompTiles();
		RefreshCompStatus();
	}

	private void StartCompStatusTimer()
	{
		try
		{
			_compStatusTimer?.Stop();
			_compStatusTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(1.0)
			};
			_compStatusTimer.Tick += delegate
			{
				if (_compStatus == null || !_compStatus.IsVisible)
				{
					return;
				}
				RefreshCompStatus();
			};
			_compStatusTimer.Start();
		}
		catch
		{
		}
	}

	private void RefreshCompStatus()
	{
		if (_compStatus == null)
		{
			return;
		}
		try
		{
			CompositionStatus st = DesktopComposition.DescribeStatus();
			string unsupported = st.Unsupported == null ? "" : string.Join("\u001f", st.Unsupported);
			string signature = string.Join("\u001e", new[]
			{
				st.ModeName ?? "",
				st.Transparency.ToString(),
				st.Shadows.ToString(),
				st.Animations.ToString(),
				st.Backend ?? "",
				st.DwmBlurGlassPolicy ?? "",
				st.DwmBlurGlassState ?? "",
				st.Status ?? "",
				st.RestoreAvailable.ToString(),
				unsupported
			});
			if (string.Equals(_compStatusSignature, signature, StringComparison.Ordinal))
			{
				return;
			}
			_compStatusSignature = signature;
			_compStatus.Children.Clear();
			_compStatus.Children.Add(StatusLine("Composition mode", st.ModeName));
			_compStatus.Children.Add(StatusLine("Transparency", st.Transparency ? "Enabled" : "Off"));
			_compStatus.Children.Add(StatusLine("Shadows", st.Shadows ? "Enabled" : "Off"));
			_compStatus.Children.Add(StatusLine("Animations", st.Animations ? "Enabled" : "Off"));
			_compStatus.Children.Add(StatusLine("Backend", st.Backend));
			_compStatus.Children.Add(StatusLine("Status", st.Status));
			_compStatus.Children.Add(StatusLine("Restore available", st.RestoreAvailable ? "Yes" : "No"));
			if (st.Unsupported != null && st.Unsupported.Count > 0)
			{
				_compStatus.Children.Add(new TextBlock
				{
					Text = "Not reproducible on other apps' windows:",
					Foreground = Text,
					FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"),
					FontSize = 13.0,
					Margin = new Thickness(0.0, 10.0, 0.0, 2.0)
				});
				foreach (string u in st.Unsupported)
				{
					_compStatus.Children.Add(new TextBlock
					{
						Text = "• " + u,
						Foreground = Text2,
						FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
						FontSize = 12.0,
						TextWrapping = TextWrapping.Wrap,
						MaxWidth = 760.0,
						Margin = new Thickness(6.0, 1.0, 0.0, 1.0)
					});
				}
			}
		}
		catch
		{
		}
	}

	private FrameworkElement StatusLine(string label, string value)
	{
		StackPanel sp = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 2.0, 0.0, 2.0)
		};
		sp.Children.Add(new TextBlock
		{
			Text = label + ":",
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Width = 150.0
		});
		sp.Children.Add(new TextBlock
		{
			Text = value,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"),
			FontSize = 13.0
		});
		return sp;
	}

	// The running shell, so launcher toggles can be applied LIVE through App-private objects (see App.ApplyXxx).
	private static App? HostApp => System.Windows.Application.Current as App;

	// The unified "Launcher" category — every persistent launcher-management setting that used to live only in the
	// tray menu / taskbar right-click, centralised here. Each row persists via SettingsStore.Update and then applies
	// the change live (directly for static side-effects, or via HostApp for App-private ones).
	private UIElement LauncherPage()
	{
		AppSettings s = SettingsStore.Load();
		ShellProfileStatus profileStatus = ShellProfileManager.Describe();
		if (string.IsNullOrWhiteSpace(_shellProfileSel))
		{
			_shellProfileSel = ShellProfileManager.Profiles.Any(p => string.Equals(p.Id, profileStatus.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
				? profileStatus.ActiveProfileId
				: ShellProfileIds.Windows81;
		}
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0),
			MaxWidth = 760.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		panel.Children.Add(SectionTitle("Launcher"));
		panel.Children.Add(new TextBlock
		{
			Text = "Everything that controls the Win8.1 layer — startup, shell takeover, taskbar, Start screen, appearance, behaviour and recovery — in one place. All changes are reversible and most apply instantly.",
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 760.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 2.0, 0.0, 12.0)
		});

		panel.Children.Add(SubHeader("Experience profile"));
		_shellProfileTiles = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		panel.Children.Add(_shellProfileTiles);
		RebuildShellProfileTiles();
		StackPanel profileActions = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 14.0, 0.0, 6.0)
		};
		profileActions.Children.Add(CompActionBtn("Apply profile", primary: true, delegate
		{
			ShellProfileApplyResult result = ShellProfileManager.Apply(_shellProfileSel);
			if (!result.Success)
			{
				ShowShellProfileFailure(result);
			}
			RebuildShellProfileTiles();
			RefreshShellProfileStatus();
		}));
		profileActions.Children.Add(CompActionBtn("Restore previous", primary: false, delegate
		{
			ShellProfileApplyResult result = ShellProfileManager.RestorePrevious();
			if (result.Success && !string.IsNullOrWhiteSpace(result.ProfileId))
			{
				_shellProfileSel = result.ProfileId;
			}
			else if (!result.Success)
			{
				ShowShellProfileFailure(result);
			}
			RebuildShellProfileTiles();
			RefreshShellProfileStatus();
		}));
		profileActions.Children.Add(CompActionBtn("Native recovery", primary: false, delegate
		{
			ShellProfileApplyResult result = ShellProfileManager.Apply(ShellProfileIds.Native);
			if (result.Success)
			{
				_shellProfileSel = ShellProfileIds.Native;
			}
			else
			{
				ShowShellProfileFailure(result);
			}
			RebuildShellProfileTiles();
			RefreshShellProfileStatus();
		}));
		profileActions.Children.Add(CompActionBtn("Reset selected", primary: false, delegate
		{
			if (System.Windows.MessageBox.Show(
				"Reset the selected experience to its built-in defaults? Common settings, Start layout and pins are preserved.",
				"Reset experience profile",
				MessageBoxButton.OKCancel,
				MessageBoxImage.Warning) == MessageBoxResult.OK)
			{
				ShellProfileApplyResult result = ShellProfileManager.ResetAndApply(_shellProfileSel);
				if (!result.Success)
				{
					ShowShellProfileFailure(result);
				}
				RebuildShellProfileTiles();
				RefreshShellProfileStatus();
			}
		}));
		panel.Children.Add(profileActions);
		_shellProfileStatus = new TextBlock
		{
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.5,
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 760.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 2.0, 0.0, 4.0)
		};
		panel.Children.Add(_shellProfileStatus);
		RefreshShellProfileStatus();

		// ===== Startup =====
		panel.Children.Add(SubHeader("Startup"));
		panel.Children.Add(ToggleRow("Start at sign-in", "Launch the Win8.1 layer automatically when you sign in (a per-user logon task, which also runs the boot supervisor that keeps the shell healthy).", LogonTask.IsEnabled(), delegate(bool on)
		{
			System.Threading.Tasks.Task.Run(delegate
			{
				bool ok = LogonTask.SetEnabled(on);
				if (ok)
				{
					SettingsStore.Update(delegate(AppSettings x) { x.RunFirstTask = on; });
				}
			});
		}));
		panel.Children.Add(ToggleRow("Also add a Run-key entry", "Legacy fallback autostart via the HKCU Run key. Normally not needed when 'Start at sign-in' is on.", Autostart.IsEnabled(), delegate(bool on)
		{
			System.Threading.Tasks.Task.Run(delegate { Autostart.SetEnabled(on); });
		}));
		panel.Children.Add(ToggleRow("Open Start at launch", "Show the Start screen as soon as the layer starts at sign-in.", s.BootToStart, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.BootToStart = on; });
		}));
		panel.Children.Add(ToggleRow("Replace the Start button", "Route the taskbar Start button and the Windows key to the Win8.1 Start screen.", s.ReplaceStartMenu, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ReplaceStartMenu = on; });
			HostApp?.ApplyReplaceStartButton(on);
		}));
		panel.Children.Add(ToggleRow("Use the Windows 7 Start menu", "Show the classic Win7 orb Start menu instead of the full-screen Metro Start. Off = Metro 8.1 Start (the default). Independent of the desktop-composition mode.", s.Win7StartMenuEnabled, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.Win7StartMenuEnabled = on; });
		}));

		// ===== Shell takeover =====
		panel.Children.Add(SubHeader("Shell takeover"));
		panel.Children.Add(ToggleRow("Dominant mode (Win8.1 takes over)", "Suspends the native Start host, replaces the taskbar and routes Win+S to the Win8.1 Search. Reversible; native is restored on exit.", s.DominantMode, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.DominantMode = on;
				x.SuspendNativeStart = on;
				x.TaskbarEnabled = on;
			});
			HostApp?.ApplyDominantMode(on);
		}));
		panel.Children.Add(ToggleRow("Win8.1 taskbar", "Replace the native taskbar with the Win8.1 taskbar.", s.TaskbarEnabled, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarEnabled = on; });
			HostApp?.ApplyTaskbarEnabled(on);
		}));
		panel.Children.Add(ToggleRow("Suspend native Start host", "Freeze StartMenuExperienceHost while the layer runs. Always resumed on exit.", s.SuspendNativeStart, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.SuspendNativeStart = on; });
			HostApp?.ApplySuspendNativeStart(on);
		}));
		panel.Children.Add(ToggleRow("Hot corners", "Win8.1 screen-corner gestures (Charms, Start, recent-app switch).", s.HotCornersEnabled, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.HotCornersEnabled = on; });
			HostApp?.ApplyHotCorners(on);
		}));

		// ===== Taskbar =====
		panel.Children.Add(SubHeader("Taskbar"));
		panel.Children.Add(ToggleRow("Performance monitor", "Show CPU / RAM / network telemetry on the taskbar.", s.PerfMonEnabled, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.PerfMonEnabled = on; });
			TaskbarWindow.ReloadPerfMode();
		}));
		panel.Children.Add(ToggleRow("Lock the taskbar", "Prevent resizing and moving the taskbar.", s.TaskbarLocked, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarLocked = on; });
			TaskbarWindow.RaiseTaskbarLayoutChanged();
		}));
		panel.Children.Add(ToggleRow("Auto-hide the taskbar", "Hide the taskbar until you point at its screen edge.", s.TaskbarAutoHide, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarAutoHide = on; });
			TaskbarWindow.RaiseAutoHideChanged();
		}));
		panel.Children.Add(SubLabel("Taskbar position"));
		string[] posOpts = new string[4] { "Bottom", "Top", "Left", "Right" };
		panel.Children.Add(Win81Dropdown("Taskbar position", posOpts, Math.Max(0, Array.IndexOf(posOpts, s.TaskbarPosition)), delegate(int i)
		{
			string v = posOpts[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarPosition = v; });
			TaskbarWindow.RaiseTaskbarPositionChanged();
		}));
		panel.Children.Add(SubLabel("Taskbar size"));
		string[] sizeOpts = new string[3] { "Small", "Medium", "Large" };
		int sizeIdx = ((s.TaskbarSize == "Small") ? 0 : ((s.TaskbarSize == "Large") ? 2 : 1));
		panel.Children.Add(Win81Dropdown("Taskbar size", sizeOpts, sizeIdx, delegate(int i)
		{
			string v = sizeOpts[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarSize = v; });
			TaskbarWindow.RaiseTaskbarSizeChanged();
		}));
		panel.Children.Add(SubLabel("Taskbar alignment"));
		string[] alignOpts = new string[2] { "Left", "Center" };
		panel.Children.Add(Win81Dropdown("Taskbar alignment", alignOpts, ((s.TaskbarAlignment == "Center") ? 1 : 0), delegate(int i)
		{
			string v = alignOpts[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarAlignment = v; });
			TaskbarWindow.RaiseTaskbarAlignmentChanged();
		}));
		panel.Children.Add(SubLabel("Combine taskbar buttons"));
		string[] combineOpts = new string[3] { "Always", "When taskbar is full", "Never" };
		string[] combineVals = new string[3] { "Always", "WhenFull", "Never" };
		panel.Children.Add(Win81Dropdown("Combine taskbar buttons", combineOpts, Math.Max(0, Array.IndexOf(combineVals, s.TaskbarCombine)), delegate(int i)
		{
			string v = combineVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarCombine = v; });
			TaskbarWindow.RaiseTaskbarLayoutChanged();
		}));
		panel.Children.Add(SubLabel("Taskbar colour"));
		string[] colorOpts = new string[3] { "From desktop wallpaper", "Match Start screen", "Transparent" };
		string[] colorVals = new string[3] { "Wallpaper", "Start", "Transparent" };
		panel.Children.Add(Win81Dropdown("Taskbar color", colorOpts, Math.Max(0, Array.IndexOf(colorVals, s.TaskbarColorMode)), delegate(int i)
		{
			string v = colorVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarColorMode = v; });
			TaskbarWindow.RaiseTaskbarColorChanged();
		}));
		panel.Children.Add(ToggleRow("Show Search button", "Show the Search button on the taskbar.", s.ShowSearch, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ShowSearch = on; });
			TaskbarWindow.RaiseTaskbarButtonsChanged();
		}));
		panel.Children.Add(ToggleRow("Show Task view button", "Show the Task view button on the taskbar.", s.ShowTaskView, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ShowTaskView = on; });
			TaskbarWindow.RaiseTaskbarButtonsChanged();
		}));
		panel.Children.Add(ToggleRow("Show Action center button", "Show the Action center button on the taskbar.", s.ShowActionCenter, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ShowActionCenter = on; });
			TaskbarWindow.RaiseTaskbarButtonsChanged();
		}));
		panel.Children.Add(SubLabel("Action center position"));
		string[] acPosOpts = new string[2] { "Far right (after date)", "Left of the clock" };
		string[] acPosVals = new string[2] { "Right", "Left" };
		panel.Children.Add(Win81Dropdown("Action center position", acPosOpts, Math.Max(0, Array.IndexOf(acPosVals, s.ActionCenterPosition)), delegate(int i)
		{
			string v = acPosVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.ActionCenterPosition = v; });
			TaskbarWindow.RaiseTaskbarButtonsChanged();
		}));
		panel.Children.Add(ToggleRow("Same taskbar on all displays", "Show the same taskbar buttons on every monitor (off = per-display Win8.1 taskbars).", s.TaskbarSameOnAllDisplays, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarSameOnAllDisplays = on; });
			TaskbarWindow.RaiseTaskbarLayoutChanged();
		}));

		// ===== Start screen =====
		panel.Children.Add(SubHeader("Start screen"));
		panel.Children.Add(SubLabel("Tile size"));
		string[] tileOpts = new string[2] { "Comfortable", "Compact" };
		double[] tileVals = new double[2] { 1.0, 0.8 };
		int tileIdx = ((Math.Abs(s.TileScale - 0.8) < 0.01) ? 1 : 0);
		panel.Children.Add(Win81Dropdown("Tile scale", tileOpts, tileIdx, delegate(int i)
		{
			double v = tileVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TileScale = v; });
			HostApp?.ApplyTileDensity(v);
		}));
		panel.Children.Add(ToggleRow("Packed grid (auto-arrange)", "Automatically pack tiles to remove gaps.", s.PackedGrid, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.PackedGrid = on; });
			HostApp?.ApplyPackedGrid(on);
		}));

		// ===== Appearance =====
		panel.Children.Add(SubHeader("Appearance"));
		panel.Children.Add(ToggleRow("Win8.1 shell icons", "Authentic Win8.1 icons for This PC, Recycle Bin, Network, Control Panel and your user folder. Fully reversible — turn OFF to restore the original Windows icons.", s.ReplaceSystemIcons, delegate(bool on) { SettingsStore.Update(delegate(AppSettings x) { x.ReplaceSystemIcons = on; }); if (on) { System.Threading.Tasks.Task.Run((System.Action)SystemIcons81.Apply); } else { System.Threading.Tasks.Task.Run((System.Action)SystemIcons81.Restore); } }));
		panel.Children.Add(ToggleRow("Win8.1 app icons", "Authentic Win8.1 / Office 2013 icons on Start tiles, the all-apps list and taskbar pins. Swaps live.", s.Replace81AppIcons, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.Replace81AppIcons = on; });
			AppIconOverrides.RefreshAllSurfaces();
		}));
		panel.Children.Add(ToggleRow("Win8.1 sounds", "Use the authentic Windows 8.1 system sounds (fully reversible).", s.UseWin81Sounds, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.UseWin81Sounds = on; });
			if (on) { SoundScheme.Apply(); } else { SoundScheme.Revert(); }
		}));
		panel.Children.Add(ToggleRow("Win8.1 cursors", "Apply the authentic Windows 8.1 pointer scheme (fully reversible).", s.UseWin81Cursors, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.UseWin81Cursors = on; });
			if (on) { CursorScheme.Apply(); } else { CursorScheme.Revert(); }
		}));
		if (WallpaperModule.AssetAvailable)
		{
			panel.Children.Add(ToggleRow("Win8.1 desktop wallpaper", "Set the authentic Win8.1 desktop wallpaper (reversible).", s.Win81Wallpaper, delegate(bool on)
			{
				if (on ? WallpaperModule.Apply() : WallpaperModule.Revert())
				{
					SettingsStore.Update(delegate(AppSettings x) { x.Win81Wallpaper = on; });
				}
			}));
		}
		panel.Children.Add(SubLabel("Motion"));
		string[] motionOpts = new string[4] { "Authentic", "Fast", "Reduced", "Off" };
		panel.Children.Add(Win81Dropdown("Motion", motionOpts, Math.Max(0, Array.IndexOf(motionOpts, s.MotionMode)), delegate(int i)
		{
			string v = motionOpts[i];
			Motion.Mode = Motion.Parse(v);
			SettingsStore.Update(delegate(AppSettings x) { x.MotionMode = v; });
		}));

		// ===== Behaviour =====
		panel.Children.Add(SubHeader("Behaviour"));
		panel.Children.Add(SubLabel("Auto-lock after inactivity"));
		string[] lockOpts = new string[4] { "Off", "After 5 minutes", "After 10 minutes", "After 15 minutes" };
		int[] lockVals = new int[4] { 0, 5, 10, 15 };
		panel.Children.Add(Win81Dropdown("Auto-lock", lockOpts, Math.Max(0, Array.IndexOf(lockVals, s.AutoLockMinutes)), delegate(int i)
		{
			int v = lockVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.AutoLockMinutes = v; });
			HostApp?.ApplyAutoLock(v);
		}));
		string shotName = (string.IsNullOrEmpty(s.ScreenshotApp) ? "Default" : System.IO.Path.GetFileName(s.ScreenshotApp));
		panel.Children.Add(RowLink("Screenshot app", "Opened by the Share charm — currently: " + shotName, delegate
		{
			System.Windows.Forms.OpenFileDialog dlg = new System.Windows.Forms.OpenFileDialog
			{
				Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
				Title = "Choose the screenshot app"
			};
			if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
			{
				string p = dlg.FileName;
				SettingsStore.Update(delegate(AppSettings x) { x.ScreenshotApp = p; });
			}
		}));

		// ===== Recovery =====
		panel.Children.Add(SubHeader("Recovery"));
		panel.Children.Add(ToggleRow("Auto-recover the shell", "Keep re-hiding any native taskbar that reappears, and restart Explorer if it dies.", s.AutoRecoverShell, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.AutoRecoverShell = on; });
		}));
		panel.Children.Add(ToggleRow("Force-reboot if Explorer can't recover", "Last resort: reboot (cancellable) if Explorer will not come back. Requires Auto-recover.", s.ForceRebootOnShellFailure, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ForceRebootOnShellFailure = on; });
		}));

		// ===== Portable profile (export / import) =====
		panel.Children.Add(SubHeader("Portable profile"));
		panel.Children.Add(SubLabel("Export your Start layout, tiles, workspaces and settings to a file — then import it on another PC. Import backs up your current setup first and applies after the launcher restarts."));
		System.Windows.Controls.StackPanel profileButtons = new System.Windows.Controls.StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		System.Windows.Controls.Button exportBtn = ActionButton("Export profile…", ExportProfileDialog);
		exportBtn.Margin = new Thickness(0.0, 0.0, 10.0, 0.0);
		profileButtons.Children.Add(exportBtn);
		profileButtons.Children.Add(ActionButton("Import profile…", ImportProfileDialog));
		panel.Children.Add(profileButtons);

		// ===== Live tiles =====
		panel.Children.Add(SubHeader("Live tiles"));
		panel.Children.Add(SubLabel("Weather tile city"));
		System.Windows.Controls.TextBox cityBox = new System.Windows.Controls.TextBox
		{
			Text = (s.WeatherCity ?? ""),
			Width = 260.0,
			Height = 32.0,
			Padding = new Thickness(8.0, 0.0, 8.0, 0.0),
			VerticalContentAlignment = VerticalAlignment.Center,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 6.0, 0.0, 6.0)
		};
		cityBox.LostKeyboardFocus += delegate
		{
			string c = cityBox.Text.Trim();
			SettingsStore.Update(delegate(AppSettings x) { x.WeatherCity = c; });
			HostApp?.ApplyWeatherCity(c);
		};
		panel.Children.Add(cityBox);
		panel.Children.Add(SubLabel("Weather temperature unit"));
		System.Windows.Controls.ComboBox weatherUnits = new System.Windows.Controls.ComboBox
		{
			ItemsSource = new string[] { "Celsius", "Fahrenheit" },
			SelectedIndex = string.Equals(s.WeatherUnits, "F", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
			Width = 260.0,
			Height = 32.0,
			Padding = new Thickness(8.0, 0.0, 8.0, 0.0),
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalContentAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		weatherUnits.SelectionChanged += delegate
		{
			string unit = weatherUnits.SelectedIndex == 1 ? "F" : "C";
			SettingsStore.Update(delegate(AppSettings x) { x.WeatherUnits = unit; });
			HostApp?.ApplyWeatherUnits(unit);
		};
		panel.Children.Add(weatherUnits);
		panel.Children.Add(SubLabel("News source (RSS / Atom)"));
		panel.Children.Add(new NewsSourcePicker { MaxWidth = 520, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 12) });
		panel.Children.Add(ActionButton("Open News", delegate { HostApp?.OpenNews(); }));
		panel.Children.Add(ActionButton("Pin News to Start", delegate { HostApp?.PinNewsTile(); }));
		panel.Children.Add(SubLabel("Web search engine"));
		panel.Children.Add(Win81Dropdown("Web search engine", new[] { "Bing", "Google" }, s.PlaceSearchEngine == "Google" ? 1 : 0,
			i => SettingsStore.Update(x => x.PlaceSearchEngine = i == 1 ? "Google" : "Bing")));
			panel.Children.Add(SubLabel("Web browser"));
			panel.Children.Add(Win81Dropdown("Web browser", new[] { "MetroBrowser", "System default" }, System.String.Equals(s.PreferredBrowser, "System", System.StringComparison.OrdinalIgnoreCase) ? 1 : 0, i => SettingsStore.Update(x => x.PreferredBrowser = i == 1 ? "System" : "MetroBrowser")));
		panel.Children.Add(ActionButton("Explore a place", () => MetroPlaceSearchWindow.ShowFor("")));
		panel.Children.Add(SubLabel("Google (Mail & Agenda)"));
		bool gConfigured = false;
		bool gSignedIn = false;
		try { gConfigured = GoogleAuth.IsConfigured; gSignedIn = GoogleAuth.IsSignedIn; } catch { }
		panel.Children.Add(SubLabel(gConfigured ? (gSignedIn ? "Google: signed in ✓" : "Google: not signed in") : "Google: not set up — add OAuth credentials first"));
		StackPanel gButtons = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 4.0, 0.0, 6.0)
		};
		gButtons.Children.Add(ActionButton(GoogleAuth.HasCalendarAccess ? "Manage Google Calendar" : "Connect Google Calendar", () => GoogleCalendarConnectWindow.ShowFor(this)));
		if (gSignedIn)
		{
			System.Windows.Controls.Button gSignOut = ActionButton("Sign out", delegate
			{
				try
				{
					GoogleAuth.SignOut();
					ToastService.Show(null, "Google", "Signed out", "The local Google connection was removed.");
				}
				catch (Exception ex)
				{
					Logger.Log("Google disconnect failed: " + ex.GetType().Name);
					GoogleCalendarConnectWindow.ShowFor(this);
					ToastService.Show(null, "Google", "Could not disconnect", "Manage the saved connection to try again.");
				}
			});
			gSignOut.Margin = new Thickness(0.0, 0.0, 12.0, 0.0);
			gButtons.Children.Add(gSignOut);
		}
		panel.Children.Add(gButtons);

		panel.Children.Add(new Border { Height = 18.0 });
		panel.Children.Add(ActionButton("Restart the layer now", delegate { Launch(LogonTask.CanonicalExe); }));

		ScrollViewer launcherScroll = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
			Content = panel
		};
		launcherScroll.SizeChanged += delegate(object _, SizeChangedEventArgs args)
		{
			panel.MaxWidth = Math.Min(760.0, Math.Max(320.0, args.NewSize.Width - panel.Margin.Left - panel.Margin.Right));
		};
		return launcherScroll;
	}

	private UIElement AccountsPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Your account"));
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 14.0, 0.0, 10.0)
		};
		Border pic = new Border
		{
			Width = 84.0,
			Height = 84.0,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.4)),
			Child = new TextBlock
			{
				Text = '\ue13d'.ToString(),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"),
				FontSize = 48.0,
				Foreground = System.Windows.Media.Brushes.White,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		row.Children.Add(pic);
		StackPanel who = new StackPanel
		{
			Margin = new Thickness(16.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBlock nameTb = new TextBlock
		{
			Text = Environment.UserName,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semilight"),
			FontSize = 24.0
		};
		who.Children.Add(nameTb);
		who.Children.Add(new TextBlock
		{
			Text = "Account on this PC",
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		});
		row.Children.Add(who);
		panel.Children.Add(row);
		LoadAccountInto(pic, nameTb);
		System.Windows.Controls.Button change = LinkButton("Change account picture", delegate
		{
			Launch("ms-settings:yourinfo");
		});
		change.Margin = new Thickness(0.0, 0.0, 0.0, 22.0);
		panel.Children.Add(change);
		panel.Children.Add(SectionTitle("Sign-in options"));
		panel.Children.Add(RowLink("Change your password, PIN or picture password", "Manage how you sign in to this PC", delegate
		{
			Launch("ms-settings:signinoptions");
		}));
		panel.Children.Add(RowLink("Other accounts", "Add or remove accounts on this PC", delegate
		{
			Launch("ms-settings:otherusers");
		}));
		panel.Children.Add(RowLink("Manage my Microsoft account", "Go to your account online", delegate
		{
			Launch("https://account.microsoft.com");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private static async Task LoadAccountInto(Border pic, TextBlock nameTb)
	{
		try
		{
			var (img, name) = await UserAccount.LoadAsync();
			if (!string.IsNullOrWhiteSpace(name))
			{
				nameTb.Text = name;
			}
			if (img != null)
			{
				pic.Child = new System.Windows.Controls.Image
				{
					Source = img,
					Stretch = Stretch.UniformToFill
				};
			}
		}
		catch
		{
		}
	}

	private UIElement EaseOfAccessPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Ease of Access"));
		panel.Children.Add(ToggleRow("Narrator", "Hear text and controls on the screen read aloud.", IsRunning("Narrator"), delegate(bool on)
		{
			ToggleProcess("narrator.exe", "Narrator", on);
		}));
		panel.Children.Add(ToggleRow("Magnifier", "Make everything on the screen bigger.", IsRunning("Magnify"), delegate(bool on)
		{
			ToggleProcess("magnify.exe", "Magnify", on);
		}));
		panel.Children.Add(ToggleRow("On-Screen Keyboard", "Type without a physical keyboard.", IsRunning("osk"), delegate(bool on)
		{
			ToggleProcess("osk.exe", "osk", on);
		}));
		panel.Children.Add(RowLink("High contrast", "Choose a high-contrast theme.", delegate
		{
			Launch("ms-settings:easeofaccess-highcontrast");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private static string G(int cp)
	{
		return ((char)cp).ToString();
	}

	private UIElement NetworkPage()
	{
		NetState81 network = NetState81.Read();
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Network"));
		StackPanel card = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 12.0, 0.0, 22.0)
		};
		card.Children.Add(new System.Windows.Controls.Image
		{
			Width = 34.0,
			Height = 34.0,
			Source = NetIcons81.For(network, 32, Colors.White),
			VerticalAlignment = VerticalAlignment.Center,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true
		});
		StackPanel st = new StackPanel
		{
			Margin = new Thickness(16.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		st.Children.Add(new TextBlock
		{
			Text = network.Kind switch
			{
				NetKind.Wifi => "Wi-Fi",
				NetKind.Ethernet => "Wired network",
				NetKind.Cellular => "Cellular",
				NetKind.Airplane => "Airplane mode",
				_ => "No network"
			},
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 18.0
		});
		st.Children.Add(new TextBlock
		{
			Text = network.Status,
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		});
		card.Children.Add(st);
		panel.Children.Add(card);
		panel.Children.Add(RowLink("Connections", "View available networks and manage connections", delegate
		{
			Launch("ms-settings:network-status");
		}));
		panel.Children.Add(RowLink("Airplane mode", "Turn wireless communication on or off", delegate
		{
			Launch("ms-settings:network-airplanemode");
		}));
		panel.Children.Add(RowLink("Proxy", "Set up a proxy server for your connections", delegate
		{
			Launch("ms-settings:network-proxy");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private void DrillIntoNetwork()
	{
		_headerLabel.Text = "Network";
		_headerLabel.FontSize = 24.0;
		_navPanel.Children.Clear();
		_netButtons.Clear();
		string[] netItems = NetItems;
		foreach (string item in netItems)
		{
			System.Windows.Controls.Button b = NavButtonBare(item);
			string sub = item;
			b.Click += delegate
			{
				NetNav(sub);
			};
			_netButtons[item] = b;
			_navPanel.Children.Add(b);
		}
		_backAction = UnDrill;
		NetNav("Connections");
	}

	private void NetNav(string sub)
	{
		_netCurrent = sub;
		foreach (var (name, btn) in _netButtons)
		{
			btn.Tag = ((name == sub) ? "sel" : null);
		}
		_content.Child = NetPage(sub);
	}

	private UIElement NetPage(string sub)
	{
		if (1 == 0)
		{
		}
		UIElement result = sub switch
		{
			"Connections" => NetConnectionsPage(), 
			"Airplane mode" => NetAirplanePage(), 
			"Proxy" => NetRoutePage("Proxy", "Set up a proxy server for Wi-Fi and Ethernet connections.", "ms-settings:network-proxy"), 
			"HomeGroup" => NetRoutePage("HomeGroup", "Share libraries and devices with people on this network.", "control /name Microsoft.HomeGroup"), 
			"Workplace" => NetRoutePage("Workplace", "Join a workplace to use company apps, networks, and resources.", "ms-settings:workplace"), 
			_ => NetRoutePage(sub, "", "ms-settings:network-status"), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private UIElement NetConnectionsPage()
	{
		NetState81 s = NetState81.Read();
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 34.0, 40.0, 24.0)
		};
		// Only surface Wi-Fi where the machine actually has a Wi-Fi radio — a radio-less desktop shows Ethernet only.
		if (NetCaps.HasWifi)
		{
			panel.Children.Add(NetHeading("Wi-Fi"));
			bool onWifi = s.Kind == NetKind.Wifi;
			NetState81 wifiState = onWifi
				? s
				: new NetState81(NetKind.Wifi, "Wi-Fi", 0, Internet: false, NetworkIconState.NotConnected);
			panel.Children.Add(NetEntry(NetIcons81.For(wifiState, 32, Colors.White), onWifi ? s.Label : "Wi-Fi", onWifi ? s.Status : "Not connected"));
			panel.Children.Add(LinkButton("Manage known networks", delegate
			{
				Launch("ms-settings:network-wifisettings");
			}));
		}
		panel.Children.Add(NetHeading("Ethernet"));
		bool eth = EthernetUp();
		NetState81 ethernetState = s.Kind == NetKind.Ethernet
			? s
			: new NetState81(NetKind.Ethernet, "Ethernet", 0, Internet: eth, eth ? NetworkIconState.Connected : NetworkIconState.CableUnplugged);
		panel.Children.Add(NetEntry(NetIcons81.For(ethernetState, 32, Colors.White), "Ethernet", (!eth) ? "Not connected" : ((s.Kind == NetKind.Ethernet) ? s.Status : "Connected")));
		panel.Children.Add(NetHeading("VPN"));
		panel.Children.Add(NetPlusRow("Add a VPN connection", delegate
		{
			Launch("ms-settings:network-vpn");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement NetAirplanePage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 34.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Airplane mode"));
		panel.Children.Add(new TextBlock
		{
			Text = "Turn off all wireless communication.",
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Margin = new Thickness(0.0, 4.0, 0.0, 16.0)
		});
		bool on = NetState81.Read().Kind == NetKind.Airplane;
		panel.Children.Add(SubLabel(on ? "On" : "Off"));
		panel.Children.Add(ActionButton("Change airplane mode", () => Launch("ms-settings:network-airplanemode")));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement NetRoutePage(string title, string desc, string uri)
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 34.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle(title));
		if (desc.Length > 0)
		{
			panel.Children.Add(new TextBlock
			{
				Text = desc,
				Foreground = Text2,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 13.0,
				Margin = new Thickness(0.0, 4.0, 0.0, 16.0),
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 560.0,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Left
			});
		}
		panel.Children.Add(ActionButton("Open settings", delegate
		{
			Launch(uri);
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private static TextBlock NetHeading(string t)
	{
		return new TextBlock
		{
			Text = t,
			Foreground = TitleAccent,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 20.0,
			Margin = new Thickness(0.0, 20.0, 0.0, 8.0)
		};
	}

	private FrameworkElement NetEntry(ImageSource icon, string name, string status)
	{
		Border tile = new Border
		{
			Width = 40.0,
			Height = 40.0,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.55)),
			VerticalAlignment = VerticalAlignment.Center
		};
		tile.Child = new System.Windows.Controls.Image
		{
			Source = icon,
			Width = 26.0,
			Height = 26.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		StackPanel txt = new StackPanel
		{
			Margin = new Thickness(14.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		txt.Children.Add(new TextBlock
		{
			Text = name,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 16.0
		});
		txt.Children.Add(new TextBlock
		{
			Text = status,
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		});
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 2.0, 0.0, 4.0)
		};
		row.Children.Add(tile);
		row.Children.Add(txt);
		return row;
	}

	private FrameworkElement NetPlusRow(string text, Action onClick)
	{
		Border tile = new Border
		{
			Width = 40.0,
			Height = 40.0,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.55)),
			VerticalAlignment = VerticalAlignment.Center
		};
		tile.Child = new TextBlock
		{
			Text = "+",
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 26.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBlock txt = new TextBlock
		{
			Text = text,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 16.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(14.0, 0.0, 0.0, 0.0)
		};
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 2.0, 0.0, 4.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Background = System.Windows.Media.Brushes.Transparent
		};
		row.Children.Add(tile);
		row.Children.Add(txt);
		row.MouseLeftButtonUp += delegate
		{
			onClick();
		};
		return row;
	}

	private static bool EthernetUp()
	{
		try
		{
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface ni in allNetworkInterfaces)
			{
				if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet || ni.OperationalStatus != OperationalStatus.Up)
				{
					continue;
				}
				foreach (GatewayIPAddressInformation gatewayAddress in ni.GetIPProperties().GatewayAddresses)
				{
					IPAddress a = gatewayAddress?.Address;
					if (a != null && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
					{
						return true;
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private UIElement TimeLanguagePage()
	{
		DateTime now = DateTime.Now;
		TimeZoneInfo tz = TimeZoneInfo.Local;
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Time and language"));
		panel.Children.Add(new TextBlock
		{
			Text = now.ToString("dddd, MMMM d, yyyy"),
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semilight"),
			FontSize = 22.0,
			Margin = new Thickness(0.0, 12.0, 0.0, 2.0)
		});
		panel.Children.Add(new TextBlock
		{
			Text = now.ToString("h:mm tt") + "   ·   " + tz.DisplayName,
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 22.0)
		});
		panel.Children.Add(RowLink("Date and time", "Change the date, time, and time zone", delegate
		{
			Launch("ms-settings:dateandtime");
		}));
		panel.Children.Add(RowLink("Region and language", "Add languages and set formats for date, time, and currency", delegate
		{
			Launch("ms-settings:regionlanguage");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement UpdateRecoveryPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Update and recovery"));
		panel.Children.Add(SubLabel("Windows Update"));
		panel.Children.Add(new TextBlock
		{
			Text = "Keep your PC up to date and secure.",
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 2.0, 0.0, 6.0)
		});
		string last = SystemInfo.WindowsUpdateLastChecked();
		if (last != null)
		{
			panel.Children.Add(new TextBlock
			{
				Text = last,
				Foreground = Text2,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 12.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
			});
		}
		panel.Children.Add(ActionButton("Check for updates", delegate
		{
			Launch("ms-settings:windowsupdate");
		}));
		panel.Children.Add(new Border
		{
			Height = 22.0
		});
		panel.Children.Add(SubLabel("Recovery"));
		panel.Children.Add(RowLink("Refresh your PC", "Reinstall Windows and keep your files", delegate
		{
			Launch("ms-settings:recovery");
		}));
		panel.Children.Add(RowLink("Reset your PC", "Remove everything and reinstall Windows", delegate
		{
			Launch("ms-settings:recovery");
		}));
		panel.Children.Add(RowLink("Advanced startup", "Start from a device or disc, change startup settings", delegate
		{
			Launch("ms-settings:recovery");
		}));
		panel.Children.Add(new Border
		{
			Height = 22.0
		});
		panel.Children.Add(SubLabel("File History"));
		panel.Children.Add(RowLink("Backup", "Back up your files with File History", delegate
		{
			Launch("ms-settings:backup");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement SearchAndAppsPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Search and apps"));
		panel.Children.Add(RowLink("Search", "Search settings and history", delegate
		{
			Launch("ms-settings:cortana");
		}));
		panel.Children.Add(RowLink("Notifications", "App notifications, lock screen, sounds", delegate
		{
			_content.Child = NotificationsPage();
		}));
		panel.Children.Add(RowLink("App sizes", "See how much space your apps are using", delegate
		{
			Launch("ms-settings:appsfeatures");
		}));
		panel.Children.Add(RowLink("Defaults", "Choose default apps for files and links", delegate
		{
			Launch("ms-settings:defaultapps");
		}));
		panel.Children.Add(RowLink("Startup apps", "Choose which apps run when you sign in", delegate
		{
			Launch("ms-settings:startupapps");
		}));
		panel.Children.Add(RowLink("Optional features", "Add or remove optional Windows features", delegate
		{
			Launch("ms-settings:optionalfeatures");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement NotificationsPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		System.Windows.Controls.Button back = LinkButton("‹ Search and apps", delegate
		{
			_content.Child = SearchAndAppsPage();
		});
		back.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		panel.Children.Add(back);
		panel.Children.Add(SectionTitle("Notifications"));
		panel.Children.Add(ToggleRow("Show app notifications", "Let apps show toast notifications while you work.", NotificationSettings.ToastEnabled, delegate(bool on)
		{
			NotificationSettings.SetToastEnabled(on);
		}));
		panel.Children.Add(ToggleRow("Show notifications on the lock screen", "Let apps show notifications on the lock screen.", NotificationSettings.LockScreenToastEnabled, delegate(bool on)
		{
			NotificationSettings.SetLockScreenToastEnabled(on);
		}));
		panel.Children.Add(new Border
		{
			Height = 8.0
		});
		panel.Children.Add(ActionButton("More notification settings", delegate
		{
			Launch("ms-settings:notifications");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private FrameworkElement ToggleRow(string title, string desc, bool on, Action<bool> onChange)
	{
		StackPanel sp = new StackPanel
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 16.0)
		};
		sp.Children.Add(new TextBlock
		{
			Text = title,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 16.0,
			TextWrapping = TextWrapping.Wrap
		});
		sp.Children.Add(new TextBlock
		{
			Text = desc,
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 1.0, 0.0, 8.0)
		});
		FrameworkElement toggle = Toggle(on, onChange);
		System.Windows.Automation.AutomationProperties.SetName(toggle, title);
		sp.Children.Add(toggle);
		return sp;
	}

	private static bool IsRunning(string procName)
	{
		try
		{
			return Process.GetProcessesByName(procName).Length != 0;
		}
		catch
		{
			return false;
		}
	}

	private static void ToggleProcess(string exe, string procName, bool on)
	{
		try
		{
			if (on)
			{
				Process.Start(new ProcessStartInfo(exe)
				{
					UseShellExecute = true
				});
				return;
			}
			Process[] processesByName = Process.GetProcessesByName(procName);
			foreach (Process p in processesByName)
			{
				try
				{
					p.Kill();
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Ease of Access '" + exe + "': " + ex.Message);
		}
	}

	private UIElement PcAndDevicesPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Personalize"));
		StackPanel tiles = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 10.0, 0.0, 8.0)
		};
		tiles.Children.Add(SettingTile("Lock screen", 59317, delegate
		{
			_content.Child = LockScreenPage();
		}));
		tiles.Children.Add(SettingTile("Account picture", 57661, delegate
		{
			Launch("ms-settings:yourinfo");
		}));
		tiles.Children.Add(StartScreenTile());
		panel.Children.Add(tiles);
		System.Windows.Controls.Button recent = LinkButton("View recently used settings", delegate
		{
		});
		recent.Margin = new Thickness(0.0, 8.0, 0.0, 24.0);
		panel.Children.Add(recent);
		panel.Children.Add(SectionTitle("Devices & display"));
		panel.Children.Add(RowLink("Display", "Resolution, orientation, multiple displays", delegate
		{
			_content.Child = DisplayPage();
		}));
		panel.Children.Add(RowLink("Bluetooth & devices", "Add and manage printers, mice, and more", delegate
		{
			Launch("ms-settings:bluetooth");
		}));
		panel.Children.Add(RowLink("Power & sleep", "Screen and sleep timeouts", delegate
		{
			_content.Child = PowerSleepPage();
		}));
		panel.Children.Add(RowLink("PC info", "PC name, edition, processor, memory", delegate
		{
			_content.Child = PcInfoPage();
		}));
		panel.Children.Add(SectionTitle("Personalization"));
		panel.Children.Add(RowLink("Background", "Choose your desktop background and fit", delegate
		{
			Launch("ms-settings:personalization-background");
		}));
		panel.Children.Add(RowLink("Colors", "Accent color, dark or light mode, transparency", delegate
		{
			Launch("ms-settings:colors");
		}));
		panel.Children.Add(RowLink("Themes", "Save and switch whole desktop themes", delegate
		{
			Launch("ms-settings:themes");
		}));
		panel.Children.Add(RowLink("Fonts", "Install and manage the fonts on this PC", delegate
		{
			Launch("ms-settings:fonts");
		}));
		panel.Children.Add(RowLink("Taskbar", "Taskbar behavior, corner icons, alignment", delegate
		{
			Launch("ms-settings:taskbar");
		}));
		panel.Children.Add(SectionTitle("System"));
		panel.Children.Add(RowLink("Sound", "Output and input devices, volume, app mixer", delegate
		{
			Launch("ms-settings:sound");
		}));
		panel.Children.Add(RowLink("Notifications & actions", "Choose which apps can send notifications", delegate
		{
			Launch("ms-settings:notifications");
		}));
		panel.Children.Add(RowLink("Storage", "See what's using space and free some up", delegate
		{
			Launch("ms-settings:storagesense");
		}));
		panel.Children.Add(RowLink("Multitasking", "Snap windows and virtual desktops", delegate
		{
			Launch("ms-settings:multitasking");
		}));
		panel.Children.Add(RowLink("About", "Device specs, Windows edition, rename this PC", delegate
		{
			Launch("ms-settings:about");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private Border StartScreenTile()
	{
		return SettingTile("Start screen", WindowsFlag(58.0, System.Windows.Media.Colors.White), delegate
		{
			_content.Child = StartScreenPage();
		});
	}

	private UIElement StartScreenPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		System.Windows.Controls.Button back = LinkButton("‹ PC & devices", delegate
		{
			_content.Child = PcAndDevicesPage();
		});
		back.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		panel.Children.Add(back);
		panel.Children.Add(SectionTitle("Start screen"));
		panel.Children.Add(SubLabel("Background"));
		WrapPanel wrap = new WrapPanel
		{
			Margin = new Thickness(0.0, 6.0, 0.0, 20.0)
		};
		try
		{
			foreach (string wp in Wallpapers.Presets())
			{
				Border thumb = new Border
				{
					Width = 96.0,
					Height = 60.0,
					Margin = new Thickness(0.0, 0.0, 8.0, 8.0),
					Cursor = System.Windows.Input.Cursors.Hand,
					BorderBrush = Freeze(4293322470u),
					BorderThickness = new Thickness(1.0),
					Background = new ImageBrush(SafeThumb(wp))
					{
						Stretch = Stretch.UniformToFill
					},
					ToolTip = System.IO.Path.GetFileNameWithoutExtension(wp)
				};
				string path = wp;
				thumb.MouseLeftButtonUp += delegate
				{
					_start.ApplyStartWallpaper(path);
				};
				wrap.Children.Add(thumb);
			}
		}
		catch
		{
		}
		panel.Children.Add(wrap);
		panel.Children.Add(SubLabel("Background color"));
		WrapPanel strip = new WrapPanel
		{
			Margin = new Thickness(0.0, 6.0, 0.0, 8.0)
		};
		string[] palette = Palette;
		foreach (string hex in palette)
		{
			Border sw = new Border
			{
				Width = 40.0,
				Height = 40.0,
				Margin = new Thickness(0.0, 0.0, 6.0, 6.0),
				Cursor = System.Windows.Input.Cursors.Hand,
				Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex))
			};
			string h = hex;
			sw.MouseLeftButtonUp += delegate
			{
				_start.ApplyStartColor(h);
			};
			strip.Children.Add(sw);
		}
		panel.Children.Add(strip);
		System.Windows.Controls.Button match = LinkButton("Match the Start screen theme", delegate
		{
			_start.SyncStartAccentToTheme();
		});
		match.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
		panel.Children.Add(match);
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement DisplayPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		System.Windows.Controls.Button back = LinkButton("‹ PC & devices", delegate
		{
			_content.Child = PcAndDevicesPage();
		});
		back.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		panel.Children.Add(back);
		panel.Children.Add(SectionTitle("Display"));
		System.Drawing.Rectangle res = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 0, 0);
		panel.Children.Add(new TextBlock
		{
			Text = $"Resolution   {res.Width} × {res.Height}",
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			Margin = new Thickness(0.0, 8.0, 0.0, 18.0)
		});
		int b = MonitorBrightness.Get();
		if (b >= 0)
		{
			panel.Children.Add(SubLabel("Brightness"));
			panel.Children.Add(HSlider(b, 0, 100, SetBrightnessThrottled));
		}
		else
		{
			panel.Children.Add(new TextBlock
			{
				Text = "Brightness can't be adjusted on this display.",
				Foreground = Text2,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 13.0,
				Margin = new Thickness(0.0, 8.0, 0.0, 8.0)
			});
		}
		panel.Children.Add(new Border
		{
			Height = 18.0
		});
		panel.Children.Add(ActionButton("Change display settings", delegate
		{
			Launch("ms-settings:display");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private FrameworkElement HSlider(int initial, int min, int max, Action<int> onChange)
	{
		Grid host = new Grid
		{
			Width = 300.0,
			Height = 30.0,
			Cursor = System.Windows.Input.Cursors.Hand,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 6.0, 0.0, 6.0),
			Background = System.Windows.Media.Brushes.Transparent
		};
		Border track = new Border
		{
			Height = 4.0,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 204, 204)),
			VerticalAlignment = VerticalAlignment.Center
		};
		Border fill = new Border
		{
			Height = 4.0,
			Background = TitleAccent,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		Border thumb = new Border
		{
			Width = 10.0,
			Height = 24.0,
			Background = TitleAccent,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center
		};
		host.Children.Add(track);
		host.Children.Add(fill);
		host.Children.Add(thumb);
		int cur = Math.Clamp(initial, min, max);
		Layout();
		bool dragging = false;
		host.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0021: Unknown result type (might be due to invalid IL or missing references)
			dragging = true;
			host.CaptureMouse();
			System.Windows.Point position = e.GetPosition(host);
			SetFromX(position.X);
		};
		host.MouseMove += delegate(object _, System.Windows.Input.MouseEventArgs e)
		{
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			if (dragging)
			{
				System.Windows.Point position = e.GetPosition(host);
				SetFromX(position.X);
			}
		};
		host.MouseLeftButtonUp += delegate
		{
			dragging = false;
			host.ReleaseMouseCapture();
		};
		return host;
		void Layout()
		{
			double frac = (double)(cur - min) / (double)(max - min);
			double x = frac * 290.0;
			fill.Width = x + 5.0;
			thumb.Margin = new Thickness(x, 0.0, 0.0, 0.0);
		}
		void SetFromX(double px)
		{
			double frac = Math.Clamp((px - 5.0) / 290.0, 0.0, 1.0);
			int v = (int)Math.Round((double)min + frac * (double)(max - min));
			if (v != cur)
			{
				cur = v;
				Layout();
				try
				{
					onChange(cur);
				}
				catch
				{
				}
			}
		}
	}

	private void SetBrightnessThrottled(int v)
	{
		OsdService.ShowBrightness(v);
		MonitorBrightness.SetThrottled(v);
	}

	private UIElement PcInfoPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		System.Windows.Controls.Button back = LinkButton("‹ PC & devices", delegate
		{
			_content.Child = PcAndDevicesPage();
		});
		back.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		panel.Children.Add(back);
		panel.Children.Add(SectionTitle("PC info"));
		panel.Children.Add(SubLabel("PC"));
		panel.Children.Add(InfoRow("PC name", SystemInfo.PcName));
		System.Windows.Controls.Button rename = ActionButton("Rename PC", delegate
		{
			Launch("SystemPropertiesComputerName.exe");
		});
		rename.Margin = new Thickness(0.0, 8.0, 0.0, 20.0);
		panel.Children.Add(rename);
		panel.Children.Add(SubLabel("Windows"));
		panel.Children.Add(InfoRow("Edition", SystemInfo.Edition));
		System.Windows.Controls.Button activate = LinkButton("View activation status", delegate
		{
			Launch("ms-settings:activation");
		});
		activate.Margin = new Thickness(0.0, 4.0, 0.0, 20.0);
		panel.Children.Add(activate);
		panel.Children.Add(SubLabel("System"));
		panel.Children.Add(InfoRow("Processor", SystemInfo.Processor));
		panel.Children.Add(InfoRow("Installed RAM", SystemInfo.InstalledRam));
		panel.Children.Add(InfoRow("System type", SystemInfo.SystemType));
		panel.Children.Add(InfoRow("Pen and touch", SystemInfo.PenAndTouch));
		panel.Children.Add(new Border
		{
			Height = 18.0
		});
		panel.Children.Add(ActionButton("System properties", delegate
		{
			Launch("ms-settings:about");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private static UIElement InfoRow(string key, string value)
	{
		Grid g = new Grid
		{
			Margin = new Thickness(0.0, 3.0, 0.0, 3.0),
			MaxWidth = 640.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(150.0)
		});
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		TextBlock k = new TextBlock
		{
			Text = key,
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Top
		};
		TextBlock v = new TextBlock
		{
			Text = value,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			TextWrapping = TextWrapping.Wrap
		};
		Grid.SetColumn(v, 1);
		g.Children.Add(k);
		g.Children.Add(v);
		return g;
	}

	private static int TimeoutIndexFor(int min)
	{
		for (int i = 0; i < TimeoutOptions.Length; i++)
		{
			if (TimeoutOptions[i].Min == min)
			{
				return i;
			}
		}
		long target = ((min <= 0) ? long.MaxValue : min);
		int best = TimeoutOptions.Length - 1;
		long bestDiff = long.MaxValue;
		for (int j = 0; j < TimeoutOptions.Length; j++)
		{
			long m = ((TimeoutOptions[j].Min == 0) ? long.MaxValue : TimeoutOptions[j].Min);
			long diff = Math.Abs(m - target);
			if (diff < bestDiff)
			{
				bestDiff = diff;
				best = j;
			}
		}
		return best;
	}

	private UIElement PowerSleepPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		System.Windows.Controls.Button back = LinkButton("‹ PC & devices", delegate
		{
			_content.Child = PcAndDevicesPage();
		});
		back.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		panel.Children.Add(back);
		panel.Children.Add(SectionTitle("Power and sleep"));
		string[] labels = Array.ConvertAll(TimeoutOptions, ((string Label, int Min) o) => o.Label);
		panel.Children.Add(SubLabel("Screen"));
		panel.Children.Add(new TextBlock
		{
			Text = "When plugged in, turn off after",
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		});
		panel.Children.Add(Win81Dropdown("Turn off display", labels, TimeoutIndexFor(PowerConfig.MonitorTimeoutAcMin()), delegate(int i)
		{
			int m = TimeoutOptions[i].Min;
			Task.Run(delegate
			{
				PowerConfig.SetMonitorTimeoutAcMin(m);
			});
		}));
		panel.Children.Add(new Border
		{
			Height = 14.0
		});
		panel.Children.Add(SubLabel("Sleep"));
		panel.Children.Add(new TextBlock
		{
			Text = "When plugged in, PC goes to sleep after",
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		});
		panel.Children.Add(Win81Dropdown("Sleep timeout", labels, TimeoutIndexFor(PowerConfig.StandbyTimeoutAcMin()), delegate(int i)
		{
			int m = TimeoutOptions[i].Min;
			Task.Run(delegate
			{
				PowerConfig.SetStandbyTimeoutAcMin(m);
			});
		}));
		panel.Children.Add(new Border
		{
			Height = 22.0
		});
		panel.Children.Add(ActionButton("More power settings", delegate
		{
			Launch("powercfg.cpl");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private FrameworkElement Win81Dropdown(string accessibleName, string[] options, int selectedIndex, Action<int> onChange)
	{
		if (!ShellSkin.GlassOn)
		{
			var combo = MetroPatternTheme.CreateComboBox(options, selectedIndex, index =>
			{
				try { onChange(index); }
				catch (Exception ex) { Logger.Log("Dropdown: " + ex.Message); }
			}, accessibleName);
			combo.Width = 260;
			combo.MaxDropDownHeight = 340;
			combo.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
			combo.Margin = new Thickness(0, 6, 0, 6);
			return combo;
		}
		int sel = Math.Clamp(selectedIndex, 0, Math.Max(0, options.Length - 1));
		TextBlock valueText = new TextBlock
		{
			Text = ((options.Length != 0) ? options[sel] : ""),
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0)
		};
		TextBlock chev = new TextBlock
		{
			Text = '\ue70d'.ToString(),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 10.0,
			Foreground = Text2,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		};
		DockPanel inner = new DockPanel
		{
			LastChildFill = true
		};
		DockPanel.SetDock(chev, Dock.Right);
		inner.Children.Add(chev);
		inner.Children.Add(valueText);
		Border box = new Border
		{
			Width = 260.0,
			Height = 32.0,
			Background = System.Windows.Media.Brushes.White,
			BorderBrush = Freeze(4286216826u),
			BorderThickness = new Thickness(1.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Child = inner,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		StackPanel list = new StackPanel
		{
			Background = System.Windows.Media.Brushes.White
		};
		Border listWrap = new Border
		{
			Background = System.Windows.Media.Brushes.White,
			BorderBrush = Freeze(4286216826u),
			BorderThickness = new Thickness(1.0),
			Width = 260.0,
			Child = list,
			Effect = new DropShadowEffect
			{
				BlurRadius = 10.0,
				ShadowDepth = 0.0,
				Opacity = 0.28,
				Color = Colors.Black
			}
		};
		Popup popup = new Popup
		{
			Child = listWrap,
			PlacementTarget = box,
			Placement = PlacementMode.Bottom,
			StaysOpen = false,
			AllowsTransparency = true,
			MaxHeight = 340.0
		};
		for (int i = 0; i < options.Length; i++)
		{
			int idx = i;
			Border row = new Border
			{
				Height = 32.0,
				Background = System.Windows.Media.Brushes.White,
				Cursor = System.Windows.Input.Cursors.Hand,
				Child = new TextBlock
				{
					Text = options[i],
					Foreground = Text,
					FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
					FontSize = 14.0,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(10.0, 0.0, 24.0, 0.0)
				}
			};
			row.MouseEnter += delegate
			{
				row.Background = Freeze(4293585642u);
			};
			row.MouseLeave += delegate
			{
				row.Background = System.Windows.Media.Brushes.White;
			};
			row.MouseLeftButtonUp += delegate
			{
				sel = idx;
				valueText.Text = options[idx];
				popup.IsOpen = false;
				try
				{
					onChange(idx);
				}
				catch
				{
				}
			};
			list.Children.Add(row);
		}
		box.MouseLeftButtonUp += delegate
		{
			popup.IsOpen = !popup.IsOpen;
		};
		Grid host = new Grid
		{
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 6.0, 0.0, 6.0)
		};
		host.Children.Add(box);
		host.Children.Add(popup);
		return host;
	}

	private UIElement LockScreenPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		System.Windows.Controls.Button back = LinkButton("‹ PC & devices", delegate
		{
			_content.Child = PcAndDevicesPage();
		});
		back.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		panel.Children.Add(back);
		panel.Children.Add(SectionTitle("Lock screen"));
		Border preview = new Border
		{
			Width = 360.0,
			Height = 203.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			BorderBrush = Freeze(4293322470u),
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(0.0, 10.0, 0.0, 18.0),
			Background = Freeze(4294111986u)
		};
		List<string> presets = Wallpapers.Presets();
		if (presets.Count > 0)
		{
			SetPreview(presets[0]);
		}
		panel.Children.Add(preview);
		panel.Children.Add(SubLabel("Choose a picture"));
		WrapPanel wrap = new WrapPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 12.0)
		};
		foreach (string wp in presets)
		{
			string path = wp;
			Border thumb = new Border
			{
				Width = 96.0,
				Height = 60.0,
				Margin = new Thickness(0.0, 0.0, 8.0, 8.0),
				Cursor = System.Windows.Input.Cursors.Hand,
				BorderBrush = Freeze(4293322470u),
				BorderThickness = new Thickness(1.0),
				Background = new ImageBrush(SafeThumb(wp))
				{
					Stretch = Stretch.UniformToFill
				}
			};
			thumb.MouseLeftButtonUp += delegate
			{
				SetPreview(path);
				ApplyLockScreen(path);
			};
			wrap.Children.Add(thumb);
		}
		panel.Children.Add(wrap);
		System.Windows.Controls.Button browse = ActionButton("Browse", delegate
		{
			Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
			{
				Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp",
				Title = "Choose a lock screen picture"
			};
			if (openFileDialog.ShowDialog() == true)
			{
				SetPreview(openFileDialog.FileName);
				ApplyLockScreen(openFileDialog.FileName);
			}
		});
		browse.Margin = new Thickness(0.0, 0.0, 0.0, 16.0);
		panel.Children.Add(browse);
		panel.Children.Add(ActionButton("Lock screen settings", delegate
		{
			Launch("ms-settings:lockscreen");
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
		void SetPreview(string path2)
		{
			try
			{
				preview.Background = new ImageBrush(SafeThumb(path2))
				{
					Stretch = Stretch.UniformToFill
				};
			}
			catch
			{
			}
		}
	}

	private async void ApplyLockScreen(string path)
	{
		try
		{
			await Windows.System.UserProfile.LockScreen.SetImageFileAsync(await StorageFile.GetFileFromPathAsync(path));
			Logger.Log("Lock screen set: " + path);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Logger.Log("Lock screen set failed (" + ex2.Message + "); routing to native.");
			Launch("ms-settings:lockscreen");
		}
	}

	private UIElement RoutePage(string title, string uri)
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle(title));
		panel.Children.Add(new TextBlock
		{
			Text = "Open the Windows configuration for this category.",
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 4.0, 0.0, 20.0),
			MaxWidth = 560.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		});
		panel.Children.Add(ActionButton("Open " + title, delegate
		{
			Launch(uri);
		}));
		return panel;
	}

	private static TextBlock SectionTitle(string text)
	{
		return new TextBlock
		{
			Text = text,
			Foreground = TitleAccent,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 30.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
	}

	private static TextBlock SubLabel(string text)
	{
		return new TextBlock
		{
			Text = text,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
	}

	private Border SettingTile(string label, int glyph, Action onClick)
	{
		Grid g = new Grid
		{
			Width = 250.0,
			Height = 150.0,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.4))
		};
		g.Children.Add(new TextBlock
		{
			Text = ((char)glyph).ToString(),
			FontFamily = new System.Windows.Media.FontFamily((glyph >= 57600 && glyph <= 57855) ? "Segoe UI Symbol" : "Segoe MDL2 Assets"),
			FontSize = 46.0,
			Foreground = System.Windows.Media.Brushes.White,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		});
		Border cap = new Border
		{
			Height = 34.0,
			VerticalAlignment = VerticalAlignment.Bottom,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.46))
		};
		cap.Child = new TextBlock
		{
			Text = label,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0)
		};
		g.Children.Add(cap);
		Border tile = new Border
		{
			Child = g
		};
		tile.MouseLeftButtonUp += delegate
		{
			onClick();
		};
		return tile;
	}

	// Overload: a custom icon element (e.g. the vector Windows flag) centred instead of a font glyph.
	private Border SettingTile(string label, UIElement icon, Action onClick)
	{
		Grid g = new Grid
		{
			Width = 250.0,
			Height = 150.0,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.4))
		};
		if (icon is FrameworkElement fe)
		{
			fe.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
			fe.VerticalAlignment = VerticalAlignment.Center;
		}
		g.Children.Add(icon);
		Border cap = new Border
		{
			Height = 34.0,
			VerticalAlignment = VerticalAlignment.Bottom,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.46))
		};
		cap.Child = new TextBlock
		{
			Text = label,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0)
		};
		g.Children.Add(cap);
		Border tile = new Border { Child = g };
		tile.MouseLeftButtonUp += delegate { onClick(); };
		return tile;
	}

	// The authentic Windows 2012 four-pane flag as a crisp vector (fill = colour). Drawn white on the monochrome-
	// glyph Personalize tiles so it matches the sibling white glyphs; the tile supplies the coloured background.
	internal static System.Windows.Shapes.Path WindowsFlag(double size, System.Windows.Media.Color fill)
	{
		return new System.Windows.Shapes.Path
		{
			Data = System.Windows.Media.Geometry.Parse("M999 521V999L458 921V521m-49 0V914L0 858V521Zm0-42H0V142L409 86m49-7V479H999V0"),
			Fill = new SolidColorBrush(fill),
			Stretch = System.Windows.Media.Stretch.Uniform,
			Width = size,
			Height = size
		};
	}

	private FrameworkElement RowLink(string title, string desc, Action onClick)
	{
		StackPanel sp = new StackPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 8.0)
		};
		sp.Children.Add(new TextBlock
		{
			Text = title,
			Foreground = TitleAccent,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 16.0
		});
		sp.Children.Add(new TextBlock
		{
			Text = desc,
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		});
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = sp,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Padding = new Thickness(0.0, 2.0, 0.0, 2.0),
			Template = FlatButtonTemplate()
		};
		if (!ShellSkin.GlassOn)
		{
			b.ClearValue(System.Windows.Controls.Control.TemplateProperty);
			b.ClearValue(System.Windows.Controls.Control.FocusVisualStyleProperty);
			b.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.CommandButton");
		}
		b.Click += delegate
		{
			onClick();
		};
		return b;
	}

	private static void LaunchArgs(string file, string args)
	{
		try
		{
			Process.Start(new ProcessStartInfo(file, args)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings run '" + file + " " + args + "': " + ex.Message);
		}
	}

	private UIElement ControlPanelPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Control Panel"));
		panel.Children.Add(RowLink("Open Control Panel", "Every Control Panel item in one window.", delegate { Launch("control.exe"); }));
		panel.Children.Add(SectionTitle("System"));
		panel.Children.Add(RowLink("System", "PC name, edition, processor, RAM and system properties.", delegate { Launch("sysdm.cpl"); }));
		panel.Children.Add(RowLink("Device Manager", "View and manage hardware devices and drivers.", delegate { Launch("devmgmt.msc"); }));
		panel.Children.Add(RowLink("Programs and Features", "Uninstall or change installed programs.", delegate { Launch("appwiz.cpl"); }));
		panel.Children.Add(RowLink("Power Options", "Choose or customise a power plan.", delegate { Launch("powercfg.cpl"); }));
		panel.Children.Add(SectionTitle("Hardware & Sound"));
		panel.Children.Add(RowLink("Sound", "Playback, recording and communication devices.", delegate { Launch("mmsys.cpl"); }));
		panel.Children.Add(RowLink("Mouse", "Buttons, pointers, wheel and pointer options.", delegate { Launch("main.cpl"); }));
		panel.Children.Add(RowLink("Devices and Printers", "View and manage devices, printers and scanners.", delegate { LaunchArgs("control.exe", "printers"); }));
		panel.Children.Add(SectionTitle("Network & Internet"));
		panel.Children.Add(RowLink("Network Connections", "View and configure network adapters.", delegate { Launch("ncpa.cpl"); }));
		panel.Children.Add(RowLink("Internet Options", "Connections, security zones and advanced settings.", delegate { Launch("inetcpl.cpl"); }));
		panel.Children.Add(RowLink("Windows Firewall", "Allow apps and configure firewall rules.", delegate { Launch("firewall.cpl"); }));
		panel.Children.Add(SectionTitle("Clock & Region"));
		panel.Children.Add(RowLink("Date and Time", "Set the clock, time zone and internet time.", delegate { Launch("timedate.cpl"); }));
		panel.Children.Add(RowLink("Region", "Formats, location and administrative language.", delegate { Launch("intl.cpl"); }));
		panel.Children.Add(SectionTitle("Administrative"));
		panel.Children.Add(RowLink("Services", "Start, stop and configure Windows services.", delegate { Launch("services.msc"); }));
		panel.Children.Add(RowLink("Task Scheduler", "Create and manage scheduled tasks.", delegate { Launch("taskschd.msc"); }));
		panel.Children.Add(RowLink("Event Viewer", "Application, security and system event logs.", delegate { Launch("eventvwr.msc"); }));
		panel.Children.Add(RowLink("Disk Management", "Create, format and manage disk partitions.", delegate { Launch("diskmgmt.msc"); }));
		panel.Children.Add(RowLink("Computer Management", "System tools, storage and services in one console.", delegate { Launch("compmgmt.msc"); }));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement PerformanceToolsPage()
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Built-in tools"));
		panel.Children.Add(PerfToolRow("Task Manager", "Processes, performance, startup and services.", new string[1] { "Taskmgr.exe" }, "", null));
		panel.Children.Add(PerfToolRow("Resource Monitor", "Live CPU, memory, disk and network usage.", new string[1] { "resmon.exe" }, "", null));
		panel.Children.Add(PerfToolRow("Performance Monitor", "Data collector sets and performance counters.", new string[1] { "perfmon.exe" }, "", null));
		panel.Children.Add(PerfToolRow("System Configuration", "Boot options, services and startup (msconfig).", new string[1] { "msconfig.exe" }, "", null));
		panel.Children.Add(SectionTitle("Windows Performance Toolkit"));
		panel.Children.Add(PerfToolRow("Windows Performance Recorder", "Record ETW traces for deep performance analysis.", new string[2] { "wprui.exe", "wpr.exe" }, "Microsoft.WindowsWDK.10.0", "https://learn.microsoft.com/windows-hardware/test/wpt/"));
		panel.Children.Add(PerfToolRow("Windows Performance Analyzer", "Analyse recorded ETW traces (WPA).", new string[1] { "wpa.exe" }, "Microsoft.WindowsWDK.10.0", "https://learn.microsoft.com/windows-hardware/test/wpt/"));
		panel.Children.Add(PerfToolRow("GPUView", "Inspect GPU/CPU scheduling from an ETW trace.", new string[2] { "GPUView\\GPUView.exe", "gpuview.exe" }, "Microsoft.WindowsWDK.10.0", "https://learn.microsoft.com/windows-hardware/drivers/display/using-gpuview"));
		panel.Children.Add(SectionTitle("Sysinternals & third-party"));
		panel.Children.Add(PerfToolRow("Process Explorer", "A powerful Task Manager replacement.", new string[2] { "procexp64.exe", "procexp.exe" }, "Microsoft.Sysinternals.ProcessExplorer", "https://learn.microsoft.com/sysinternals/downloads/process-explorer"));
		panel.Children.Add(PerfToolRow("Process Monitor", "Live file, registry and process activity.", new string[2] { "Procmon64.exe", "Procmon.exe" }, "Microsoft.Sysinternals.ProcessMonitor", "https://learn.microsoft.com/sysinternals/downloads/procmon"));
		panel.Children.Add(PerfToolRow("LatencyMon", "Real-time DPC/ISR latency monitoring.", new string[2] { "LatMon.exe", "LatencyMon.exe" }, "Resplendence.LatencyMon", "https://www.resplendence.com/latencymon"));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private UIElement ExperimentsPage()
	{
		ExperimentAuditReport audit = ExperimentRegistry.Audit(persist: false);
		AppSettings settings = SettingsStore.Load();
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(40.0, 40.0, 40.0, 24.0)
		};
		panel.Children.Add(SectionTitle("Experiments"));
		panel.Children.Add(ExperimentStatusRow("Windows build", audit.BuildFingerprint + (string.IsNullOrWhiteSpace(audit.DisplayVersion) ? "" : "  (" + audit.DisplayVersion + ")"), "available"));
		panel.Children.Add(ExperimentStatusRow("Mutation policy", audit.MutationPolicy, audit.MutationPolicy == "allowlist-only" ? "available" : "locked"));
		panel.Children.Add(ToggleRow("Enable allowlisted experiments", "Permits only experiments with build gates and a tested capture/apply/verify/undo path.", settings.EnableExperimentalFeatures, delegate(bool enabled)
		{
			SettingsStore.Update(delegate(AppSettings s) { s.EnableExperimentalFeatures = enabled; });
		}));
		panel.Children.Add(SectionTitle("Capabilities"));
		foreach (ExperimentCapability capability in audit.Capabilities)
		{
			panel.Children.Add(ExperimentStatusRow(capability.Name, capability.Detail, capability.Status));
		}
		panel.Children.Add(SectionTitle("Diagnostics"));
		panel.Children.Add(RowLink("Export compatibility audit", "Write the current OS, API and power-policy snapshot.", delegate
		{
			string path = ExperimentRegistry.WriteAuditReport();
			LaunchArgs("explorer.exe", "/select,\"" + path + "\"");
		}));
		panel.Children.Add(RowLink("Open experiment journal", "Review versioned experiment transactions and rollback records.", delegate
		{
			Directory.CreateDirectory(ExperimentRegistry.JournalPath);
			Launch(ExperimentRegistry.JournalPath);
		}));
		return new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = panel
		};
	}

	private void RebuildShellProfileTiles()
	{
		if (_shellProfileTiles == null)
		{
			return;
		}
		_shellProfileTiles.Children.Clear();
		foreach (ShellProfileDescriptor profile in ShellProfileManager.Profiles)
		{
			ShellProfileDescriptor selectedProfile = profile;
			bool selected = string.Equals(profile.Id, _shellProfileSel, StringComparison.OrdinalIgnoreCase);
			_shellProfileTiles.Children.Add(CompModeTile(profile.Name, profile.Glyph, selected, delegate
			{
				_shellProfileSel = selectedProfile.Id;
				RebuildShellProfileTiles();
			}));
		}
	}

	private void RefreshShellProfileStatus()
	{
		if (_shellProfileStatus == null)
		{
			return;
		}
		ShellProfileStatus status = ShellProfileManager.Describe();
		string text = $"Active: {status.ActiveProfileName}{(status.IsCustomized ? " (customized)" : string.Empty)} | Saved slots: {status.SavedSlots} | Last result: {status.LastResult}";
		if (status.RecoveryPending)
		{
			text += " | Recovery pending";
		}
		if (!string.IsNullOrWhiteSpace(status.LastError))
		{
			text += " | " + status.LastError;
		}
		_shellProfileStatus.Text = text;
	}

	private static void ShowShellProfileFailure(ShellProfileApplyResult result)
	{
		System.Windows.MessageBox.Show(
			result.Message,
			"Experience profile",
			MessageBoxButton.OK,
			MessageBoxImage.Error);
	}

	private static FrameworkElement ExperimentStatusRow(string title, string detail, string status)
	{
		Grid grid = new Grid
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 10.0),
			MaxWidth = 620.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel copy = new StackPanel();
		copy.Children.Add(new TextBlock
		{
			Text = title,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 16.0
		});
		copy.Children.Add(new TextBlock
		{
			Text = detail,
			Foreground = Text2,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 480.0,
			Margin = new Thickness(0.0, 1.0, 16.0, 0.0)
		});
		Grid.SetColumn(copy, 0);
		grid.Children.Add(copy);
		TextBlock state = new TextBlock
		{
			Text = status,
			Foreground = status == "available" ? Freeze(4278233685u) : (status == "locked" ? Freeze(4292519424u) : Text2),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"),
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(12.0, 2.0, 0.0, 0.0)
		};
		Grid.SetColumn(state, 1);
		grid.Children.Add(state);
		return grid;
	}

	private FrameworkElement PerfToolRow(string title, string desc, string[] exeCandidates, string wingetId, string? url)
	{
		string found = FindTool(exeCandidates);
		if (found != null)
		{
			string open = found;
			return RowLink(title, desc + "   ·   Installed", delegate { Launch(open); });
		}
		return RowLink(title, desc + "   ·   Not installed — click to install", delegate { OfferInstall(title, wingetId, url); });
	}

	private static string? FindTool(string[] candidates)
	{
		foreach (string c in candidates)
		{
			try
			{
				if (System.IO.Path.IsPathRooted(c))
				{
					if (System.IO.File.Exists(c))
					{
						return c;
					}
					continue;
				}
				string sys = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), c);
				if (System.IO.File.Exists(sys))
				{
					return sys;
				}
				string[] pfs = new string[2] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
				foreach (string pf in pfs)
				{
					if (!string.IsNullOrEmpty(pf))
					{
						string wpt = System.IO.Path.Combine(pf, "Windows Kits", "10", "Windows Performance Toolkit", c);
						if (System.IO.File.Exists(wpt))
						{
							return wpt;
						}
					}
				}
				string path = Environment.GetEnvironmentVariable("PATH") ?? "";
				foreach (string dir in path.Split(';'))
				{
					if (string.IsNullOrWhiteSpace(dir))
					{
						continue;
					}
					try
					{
						string p = System.IO.Path.Combine(dir.Trim(), c);
						if (System.IO.File.Exists(p))
						{
							return p;
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static void OfferInstall(string title, string wingetId, string? url)
	{
		try
		{
			bool hasWinget = !string.IsNullOrEmpty(wingetId);
			string msg = "\"" + title + "\" is not installed.\n\n" + (hasWinget ? "Install it now with winget?" : "Open the download page?");
			System.Windows.MessageBoxResult r = System.Windows.MessageBox.Show(msg, "Install " + title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
			if (r != System.Windows.MessageBoxResult.Yes)
			{
				return;
			}
			if (hasWinget)
			{
				Process.Start(new ProcessStartInfo("cmd.exe", "/k winget install --id " + wingetId + " -e --accept-package-agreements --accept-source-agreements")
				{
					UseShellExecute = true
				});
			}
			else if (!string.IsNullOrEmpty(url))
			{
				Launch(url);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings install '" + title + "': " + ex.Message);
		}
	}

	public FrameworkElement Toggle(bool on, Action<bool> onChange)
	{
		if (!ShellSkin.GlassOn)
			return MetroPatternTheme.CreateToggle(on, value =>
			{
				try { onChange(value); }
				catch (Exception ex) { Logger.Log("Toggle: " + ex.Message); }
			}, "Toggle switch");
		SolidColorBrush accent = new SolidColorBrush(SettingsPane.AccentToneColor(0.5));
		System.Windows.Media.Brush outline = Freeze(4289703855u);
		System.Windows.Media.Brush knobBrush = Freeze(4279900698u);
		TextBlock state = new TextBlock
		{
			Text = (on ? "On" : "Off"),
			FontWeight = FontWeights.SemiBold,
			Foreground = Text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Width = 34.0
		};
		Border inner = new Border
		{
			Background = (on ? accent : System.Windows.Media.Brushes.White)
		};
		Border track = new Border
		{
			Width = 44.0,
			Height = 18.0,
			BorderThickness = new Thickness(1.0),
			BorderBrush = outline,
			Background = System.Windows.Media.Brushes.White,
			Padding = new Thickness(1.0),
			Child = inner,
			VerticalAlignment = VerticalAlignment.Center
		};
		Border knob = new Border
		{
			Width = 11.0,
			Height = 18.0,
			Background = knobBrush,
			HorizontalAlignment = (on ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left),
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid sw = new Grid
		{
			Width = 44.0,
			Height = 18.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		sw.Children.Add(track);
		sw.Children.Add(knob);
		bool cur = on;
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Cursor = System.Windows.Input.Cursors.Hand
		};
		row.Children.Add(state);
		row.Children.Add(sw);
		row.MouseLeftButtonUp += delegate
		{
			cur = !cur;
			state.Text = (cur ? "On" : "Off");
			inner.Background = (cur ? accent : System.Windows.Media.Brushes.White);
			knob.HorizontalAlignment = (cur ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left);
			try
			{
				onChange(cur);
			}
			catch (Exception ex)
			{
				Logger.Log("Toggle: " + ex.Message);
			}
		};
		return row;
	}

	private void ExportProfileDialog()
	{
		try
		{
			Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog
			{
				Title = "Export launcher profile",
				FileName = "launcher-profile.json",
				Filter = "Launcher profile (*.json)|*.json|All files (*.*)|*.*",
				AddExtension = true,
				DefaultExt = ".json"
			};
			if (dlg.ShowDialog(this) == true)
			{
				bool ok = LauncherProfile.Export(dlg.FileName, out string? err);
				System.Windows.MessageBox.Show(this, ok ? "Profile exported." : ("Export failed:\n" + err), "Export profile", System.Windows.MessageBoxButton.OK, ok ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Export profile dialog: " + ex.Message);
		}
	}

	private void ImportProfileDialog()
	{
		try
		{
			Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
			{
				Title = "Import launcher profile",
				Filter = "Launcher profile (*.json)|*.json|All files (*.*)|*.*",
				CheckFileExists = true
			};
			if (dlg.ShowDialog(this) != true)
			{
				return;
			}
			System.Windows.MessageBoxResult confirm = System.Windows.MessageBox.Show(this,
				"Import this profile?\n\nYour current layout and settings will be backed up, then replaced. Changes apply after the launcher restarts.",
				"Import profile", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
			if (confirm != System.Windows.MessageBoxResult.OK)
			{
				return;
			}
			bool ok = LauncherProfile.Import(dlg.FileName, out string? err);
			System.Windows.MessageBox.Show(this,
				ok ? "Profile imported. Restart the launcher to apply." : ("Import rejected — your current setup was kept:\n" + err),
				"Import profile", System.Windows.MessageBoxButton.OK, ok ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
		}
		catch (Exception ex)
		{
			Logger.Log("Import profile dialog: " + ex.Message);
		}
	}

	private System.Windows.Controls.Button ActionButton(string text, Action onClick)
	{
		if (!ShellSkin.GlassOn)
		{
			var button = new System.Windows.Controls.Button { Content = text, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
			button.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.Button");
			button.Click += (_, _) => onClick();
			return button;
		}
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			Foreground = Text,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderBrush = Freeze(4288256409u),
			BorderThickness = new Thickness(2.0),
			Padding = new Thickness(18.0, 7.0, 18.0, 7.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null
		};
		b.MouseEnter += delegate
		{
			b.BorderBrush = TitleAccent;
			b.Foreground = TitleAccent;
		};
		b.MouseLeave += delegate
		{
			b.BorderBrush = Freeze(4288256409u);
			b.Foreground = Text;
		};
		b.Click += delegate
		{
			onClick();
		};
		return b;
	}

	private System.Windows.Controls.Button LinkButton(string text, Action onClick)
	{
		if (!ShellSkin.GlassOn)
		{
			var button = new System.Windows.Controls.Button { Content = text, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
			button.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.LinkButton");
			button.Click += (_, _) => onClick();
			return button;
		}
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			Foreground = TitleAccent,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Template = FlatButtonTemplate(),
			Padding = new Thickness(0.0, 4.0, 0.0, 4.0)
		};
		b.Click += delegate
		{
			onClick();
		};
		return b;
	}

	private static BitmapImage SafeThumb(string path)
	{
		BitmapImage bmp = new BitmapImage();
		bmp.BeginInit();
		bmp.UriSource = new Uri(path);
		bmp.CacheOption = BitmapCacheOption.OnLoad;
		bmp.DecodePixelWidth = 120;
		bmp.EndInit();
		((Freezable)bmp).Freeze();
		return bmp;
	}

	private static void Launch(string uri)
	{
		try
		{
			Process.Start(new ProcessStartInfo(uri)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings open '" + uri + "': " + ex.Message);
		}
	}

	private static ControlTemplate FlatButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		t.VisualTree = cp;
		return t;
	}

	private static ControlTemplate NavTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(24, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		Trigger sel = new Trigger
		{
			Property = FrameworkElement.TagProperty,
			Value = "sel"
		};
		sel.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(46, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(sel);
		return t;
	}

	public void QaRender(string outPath, string category = "PC & devices", double w = 1366.0, double h = 768.0)
	{
		// A realized HwndSource is required for nested ScrollViewer content to render. The QA window remains outside
		// the virtual desktop, but Show() prevents false-white screenshots that could hide a visual regression.
		Width = w;
		Height = h;
		if (!IsVisible)
		{
			Show();
		}
		switch (category)
		{
		case "Display":
			Nav("PC & devices");
			_content.Child = DisplayPage();
			break;
		case "PC info":
			Nav("PC & devices");
			_content.Child = PcInfoPage();
			break;
		case "Power & sleep":
			Nav("PC & devices");
			_content.Child = PowerSleepPage();
			break;
		case "Lock screen":
			Nav("PC & devices");
			_content.Child = LockScreenPage();
			break;
		case "Notifications":
			Nav("Search and apps");
			_content.Child = NotificationsPage();
			break;
		case "Ease of Access":
			DrillIntoEoa();
			EoaNav("Keyboard");
			break;
		default:
			Nav(category);
			break;
		}
		if (_content.Child is UIElement page)
		{
			page.BeginAnimation(UIElement.OpacityProperty, null);
			page.Opacity = 1.0;
			if (page.RenderTransform is TranslateTransform slide)
			{
				slide.BeginAnimation(TranslateTransform.YProperty, null);
				slide.Y = 0.0;
			}
		}
		FrameworkElement grid = (FrameworkElement)base.Content;
		grid.ApplyTemplate();
		grid.Measure(new System.Windows.Size(w, h));
		grid.Arrange(new Rect(0.0, 0.0, w, h));
		grid.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap((int)w, (int)h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(grid);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using FileStream fs = File.Create(outPath);
		enc.Save(fs);
	}

	// Full-height content render for QA: renders a page's ScrollViewer body at its natural height on white.
	public void QaRenderTall(string outPath, string category, double w = 900.0)
	{
		if (BuildPage(category) is ScrollViewer scr)   // fresh, UNPARENTED element renders correctly offscreen
		{
			scr.Background = System.Windows.Media.Brushes.White;
			scr.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
			scr.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
			scr.Width = w;
			scr.Measure(new System.Windows.Size(w, double.PositiveInfinity));
			double hh = Math.Min(8000.0, Math.Max(400.0, scr.DesiredSize.Height));
			scr.Arrange(new Rect(0.0, 0.0, w, hh));
			scr.UpdateLayout();
			RenderTargetBitmap rtb = new RenderTargetBitmap((int)w, (int)hh, 96.0, 96.0, PixelFormats.Pbgra32);
			rtb.Render(scr);
			PngBitmapEncoder enc = new PngBitmapEncoder();
			enc.Frames.Add(BitmapFrame.Create(rtb));
			using FileStream fs = File.Create(outPath);
			enc.Save(fs);
		}
	}
}
