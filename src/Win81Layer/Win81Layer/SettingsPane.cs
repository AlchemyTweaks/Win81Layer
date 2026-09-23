using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class SettingsPane : Window
{
	private struct ACCENT_POLICY
	{
		public int AccentState;

		public int AccentFlags;

		public uint GradientColor;

		public int AnimationId;
	}

	private struct WINCOMPATTRDATA
	{
		public int Attribute;

		public nint Data;

		public int SizeOfData;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct PHYSICAL_MONITOR
	{
		public nint h;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
		public char[] desc;
	}

	private enum QAKind
	{
		Launch,
		Flyout,
		Toggle,
		Network,
		TextGlyph
	}

	private sealed record QuickAction(string Id, string Label, int Glyph, bool DefaultHidden, QAKind Kind = QAKind.Launch);

	private const double PaneWidth = 346.0;

	private const string Mdl2 = "Segoe MDL2 Assets";

	private const string IconFont = "Segoe MDL2 Assets";

	private const int MNetWired = 59449;

	private const int MWifi = 59137;

	private const int MNetOffline = 62340;

	private const int MAirplane = 59145;

	private const int MVol0 = 59794;

	private const int MVol1 = 59795;

	private const int MVol2 = 59796;

	private const int MVol3 = 59797;

	private const int MMute = 59215;

	private const int MBright = 59142;

	private const int MNotif = 59367;

	private const int MPower = 59368;

	private readonly Border _root;

	private readonly TranslateTransform _slide = new TranslateTransform(346.0, 0.0);

	private readonly AudioController _audio = new AudioController();

	private bool _hiding;

	private Popup? _flyout;

	private TextBlock? _volGlyphTb;

	private TextBlock? _volLabelTb;

	private TextBlock? _brightLabelTb;

	private TextBlock? _netLabelTb;

	private System.Windows.Controls.Image? _netImage;

	private System.Windows.Controls.Button? _brightTile;

	private System.Windows.Controls.Button? _volTile;

	private System.Windows.Controls.Button? _powerTile;

	private DispatcherTimer? _liveTimer;

	private int _liveTick;

	private const int WCA_ACCENT_POLICY = 19;

	private const int ACCENT_DISABLED = 0;

	private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

	private static readonly QuickAction[] Catalog = new QuickAction[15]
	{
		new QuickAction("network", "Network", 59137, DefaultHidden: false, QAKind.Network),
		new QuickAction("volume", "Volume", 59797, DefaultHidden: false, QAKind.Flyout),
		new QuickAction("brightness", "Brightness", 59142, DefaultHidden: false, QAKind.Flyout),
		new QuickAction("notifications", "Notifications", 59367, DefaultHidden: false),
		new QuickAction("power", "Power", 59368, DefaultHidden: false, QAKind.Flyout),
		new QuickAction("keyboard", "Keyboard", 0, DefaultHidden: false, QAKind.TextGlyph),
		new QuickAction("airplane", "Airplane\nmode", 59145, DefaultHidden: true, QAKind.Toggle),
		new QuickAction("bluetooth", "Bluetooth", 59138, DefaultHidden: true, QAKind.Toggle),
		new QuickAction("nightlight", "Night light", 59142, DefaultHidden: true),
		new QuickAction("vpn", "VPN", 59455, DefaultHidden: true),
		new QuickAction("batterysaver", "Battery\nsaver", 60352, DefaultHidden: true),
		new QuickAction("project", "Project", 59380, DefaultHidden: true),
		new QuickAction("connect", "Connect", 59139, DefaultHidden: true),
		new QuickAction("hotspot", "Mobile\nhotspot", 59643, DefaultHidden: true),
		new QuickAction("focusassist", "Focus\nassist", 60032, DefaultHidden: true)
	};

	private readonly List<(TextBlock glyph, Func<bool> on)> _extToggles = new List<(TextBlock, Func<bool>)>();

	private bool _extAirOn;

	private bool _extBtOn;

	private bool _toggling;

	private bool _customising;

	private bool? _qaForceCompact;

	private bool? _qaForceBlur;

	private double _tileH = 84.0;

	private double _glyphEm = 22.0;

	private double _textEm = 17.0;

	private double _labelEm = 12.0;

	private double _iconH = 28.0;

	private double _bmpSize = 22.0;

	private const string CharmRowFmt = "Win81CharmRow";

	private System.Windows.Point _charmDragStart;

	private string? _charmDragId;

	public event Action? PersonalizeRequested;

	public event Action? ChangePcSettingsRequested;

	private static string G(int cp)
	{
		return ((char)cp).ToString();
	}

	private static string InputLanguage()
	{
		try
		{
			CultureInfo c = System.Windows.Forms.InputLanguage.CurrentInputLanguage.Culture;
			return (c.TwoLetterISOLanguageName == "en") ? "ENG" : c.TwoLetterISOLanguageName.ToUpperInvariant();
		}
		catch
		{
			return "ENG";
		}
	}

	private static (ImageSource? Img, string Label) NetState()
	{
		NetState81 s = NetState81.Read();
		ImageSource img = NetIcons81.For(s, 24, Colors.White);
		NetKind kind = s.Kind;
		if (1 == 0)
		{
		}
		string text = kind switch
		{
			NetKind.Wifi => s.Label, 
			NetKind.Cellular => s.Label, 
			NetKind.Ethernet => "Ethernet", 
			NetKind.Airplane => "Airplane mode", 
			_ => "No network", 
		};
		if (1 == 0)
		{
		}
		string label = text;
		return (Img: img, Label: label);
	}

	private (string Glyph, string Label) VolState()
	{
		bool muted = _audio.GetMute();
		int pct = (int)Math.Round(_audio.GetVolume() * 100f);
		if (muted)
		{
			return (Glyph: G(59215), Label: "Muted");
		}
		int g = ((pct == 0) ? 59794 : ((pct <= 33) ? 59795 : ((pct <= 66) ? 59796 : 59797)));
		return (Glyph: G(g), Label: pct.ToString());
	}

	public SettingsPane()
	{
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.AllowsTransparency = true;
		base.Background = System.Windows.Media.Brushes.Transparent;
		base.Topmost = true;
		base.Width = 346.0;
		base.Title = "Settings";
		_root = new Border
		{
			Background = PaneBg(),
			RenderTransform = _slide
		};
		base.Content = _root;
		base.Deactivated += delegate
		{
			Popup? flyout = _flyout;
			if ((flyout == null || !flyout.IsOpen) && !_toggling)
			{
				HidePane();
			}
		};
		base.PreviewKeyDown += OnKey;
		base.PreviewMouseLeftButtonUp += delegate
		{
			_charmDragId = null;
		};
	}

	internal static System.Windows.Media.Brush PaneBg()
	{
		AppSettings s = SettingsStore.Load();
		System.Windows.Media.Color c = AccentToneColor(0.34);
		if (ShellSkin.GlassOn)
		{
			// Win7-Aero shell skin: a translucent smoked-glass accent tint (wallpaper/content shows through) via a
			// plain alpha brush — NOT WCA acrylic, which leaks a stuck DWM region that outlives the window/process.
			// The "Blur intensity" (shell tint opacity) slider on the Desktop Composition page drives the alpha.
			System.Windows.Media.Color g = AccentToneColor(0.30);
			// Aero glass must stay READABLE: keep panes mostly opaque (min ~69%), let the Blur slider only add
			// density on top rather than thin the pane to near-transparent.
			byte a = (byte)System.Math.Clamp(0xC0 + s.DeskCompBlur * 0x30 / 100, 0xB0, 0xF2);
			return new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, g.R, g.G, g.B));
		}
		if (s.CharmBlur || s.CharmTransparent)
		{
			// Both the "Acrylic" (CharmBlur) and "Transparent" pills now render as a plain translucent alpha tint.
			// (CharmBlur used to be a 30-alpha wash that was only visible because WCA acrylic painted behind it;
			// WCA is excised, so it falls through to the same translucent brush as CharmTransparent.)
			c = System.Windows.Media.Color.FromArgb(200, c.R, c.G, c.B);
		}
		return new SolidColorBrush(c);
	}

	internal static System.Windows.Media.Color AccentToneColor(double lightness)
	{
		System.Windows.Media.Color a = StartAccent.Color();
		var (h, s, _) = ToHsl(a);
		return FromHsl(h, Math.Clamp(s, 0.46, 0.72), lightness);
	}

	private static (double h, double s, double l) ToHsl(System.Windows.Media.Color c)
	{
		double r = (double)(int)c.R / 255.0;
		double g = (double)(int)c.G / 255.0;
		double b = (double)(int)c.B / 255.0;
		double max = Math.Max(r, Math.Max(g, b));
		double min = Math.Min(r, Math.Min(g, b));
		double h = 0.0;
		double l = (max + min) / 2.0;
		double d = max - min;
		double s;
		if (d == 0.0)
		{
			s = 0.0;
		}
		else
		{
			s = ((l > 0.5) ? (d / (2.0 - max - min)) : (d / (max + min)));
			h = ((max == r) ? ((g - b) / d + (double)((g < b) ? 6 : 0)) : ((max != g) ? ((r - g) / d + 4.0) : ((b - r) / d + 2.0)));
			h /= 6.0;
		}
		return (h: h, s: s, l: l);
	}

	private static System.Windows.Media.Color FromHsl(double h, double s, double l)
	{
		double r;
		double g;
		double b;
		double q;
		double p;
		if (s == 0.0)
		{
			r = (g = (b = l));
		}
		else
		{
			q = ((l < 0.5) ? (l * (1.0 + s)) : (l + s - l * s));
			p = 2.0 * l - q;
			r = Hue(h + 1.0 / 3.0);
			g = Hue(h);
			b = Hue(h - 1.0 / 3.0);
		}
		return System.Windows.Media.Color.FromRgb((byte)(r * 255.0), (byte)(g * 255.0), (byte)(b * 255.0));
		double Hue(double t)
		{
			if (t < 0.0)
			{
				t++;
			}
			if (t > 1.0)
			{
				t--;
			}
			if (t < 1.0 / 6.0)
			{
				return p + (q - p) * 6.0 * t;
			}
			if (t < 0.5)
			{
				return q;
			}
			if (t < 2.0 / 3.0)
			{
				return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
			}
			return p;
		}
	}

	private void OnKey(object? sender, System.Windows.Input.KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Invalid comparison between Unknown and I4
		if ((int)e.Key == 13)
		{
			if (_customising)
			{
				_customising = false;
				Build();
			}
			else
			{
				HidePane();
			}
		}
	}

	[DllImport("user32.dll")]
	private static extern int SetWindowCompositionAttribute(nint hwnd, ref WINCOMPATTRDATA data);

	private void ApplyBackdrop()
	{
		// WCA acrylic excised: SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND) leaks a stuck DWM
		// blur region that outlives the window and the whole process (ghost on the desktop, only a DWM restart
		// clears it). The pane's translucent look now comes purely from PaneBg() (a plain WPF alpha brush). This
		// only ever DISABLES any accent region left on the HWND.
		try
		{
			nint hwnd = new WindowInteropHelper(this).Handle;
			if (hwnd == IntPtr.Zero)
			{
				return;
			}
			AcrylicGlass.Clear(hwnd);
		}
		catch (Exception ex)
		{
			Logger.Log("ApplyBackdrop: " + ex.Message);
		}
	}

	public void ShowPane()
	{
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cf: Expected O, but got Unknown
		_hiding = false;
		_customising = false;
		_root.Background = PaneBg();
		Build();
		Show();
		ApplyBackdrop();
		Screen screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		Rectangle b = screen.Bounds;
		// Use the TARGET monitor's DPI (cursor's screen), not the window's current TransformToDevice (see MonitorDpi).
		double sx = MonitorDpi.ScaleFor(b);
		double sy = sx;
		if (sx <= 0.0)
		{
			sx = 1.0;
		}
		if (sy <= 0.0)
		{
			sy = 1.0;
		}
		base.Height = (double)b.Height / sy;
		base.Top = (double)b.Top / sy;
		base.Left = (double)b.Right / sx - base.Width;
		WindowUtil.ForceForeground(this);
		_slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, Motion.Dur(Motion.Cat.EdgeEnter))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeEnter)
		});
		_liveTick = 0;
		if (_liveTimer == null)
		{
			_liveTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(400L)
			};
		}
		_liveTimer.Tick -= LiveTick;
		_liveTimer.Tick += LiveTick;
		_liveTimer.Start();
	}

	private void LiveTick(object? sender, EventArgs e)
	{
		if (!base.IsVisible)
		{
			DispatcherTimer? liveTimer = _liveTimer;
			if (liveTimer != null)
			{
				liveTimer.Stop();
			}
		}
		else
		{
			if (_customising)
			{
				return;
			}
			try
			{
				(string, string) vol = VolState();
				if (_volGlyphTb != null)
				{
					_volGlyphTb.Text = vol.Item1;
				}
				if (_volLabelTb != null)
				{
					_volLabelTb.Text = vol.Item2;
				}
			}
			catch
			{
			}
			if (_liveTick % 3 == 0)
			{
				try
				{
					(ImageSource, string) net = NetState();
					if (_netImage != null)
					{
						_netImage.Source = net.Item1;
					}
					if (_netLabelTb != null)
					{
						_netLabelTb.Text = net.Item2;
					}
				}
				catch
				{
				}
			}
			if (++_liveTick % 5 != 0 || _brightLabelTb == null)
			{
				return;
			}
			Task.Run(delegate
			{
				string s = BrightnessLabel();
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					if (_brightLabelTb != null)
					{
						_brightLabelTb.Text = s;
					}
					if (_brightTile != null)
					{
						_brightTile.Opacity = ((s == "Unavailable") ? 0.5 : 1.0);
					}
				}, Array.Empty<object>());
			});
		}
	}

	public void HidePane()
	{
		if (_hiding || !base.IsVisible)
		{
			return;
		}
		_hiding = true;
		DispatcherTimer? liveTimer = _liveTimer;
		if (liveTimer != null)
		{
			liveTimer.Stop();
		}
		if (_flyout != null)
		{
			_flyout.IsOpen = false;
		}
		DoubleAnimation slide = new DoubleAnimation(346.0, Motion.Dur(Motion.Cat.EdgeExit))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeExit)
		};
		slide.Completed += delegate
		{
			if (_hiding)
			{
				Hide();
				_hiding = false;
			}
		};
		_slide.BeginAnimation(TranslateTransform.XProperty, slide);
	}

	private void Act(Action a)
	{
		HidePane();
		try
		{
			a();
		}
		catch (Exception value)
		{
			Logger.Log($"Settings action: {value}");
		}
	}

	public void QaRender(string outPath, double height = 1040.0, bool customise = false, bool? compact = null)
	{
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		_customising = customise;
		_qaForceCompact = compact;
		_root.Background = PaneBg();
		Build();
		_slide.X = 0.0;
		_root.Measure(new System.Windows.Size(346.0, height));
		_root.Arrange(new Rect(0.0, 0.0, 346.0, height));
		_root.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap(346, (int)height, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(_root);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using FileStream fs = File.Create(outPath);
		enc.Save(fs);
		Logger.Log("QaRender settings pane -> " + outPath);
		_customising = false;
		_qaForceCompact = null;
	}

	public void QaShowBlur()
	{
		_qaForceBlur = true;
		_customising = false;
		Build();
		Show();
		Rectangle scr = Screen.PrimaryScreen.Bounds;
		base.Height = scr.Height;
		base.Top = scr.Top;
		base.Left = (double)scr.Right - base.Width;
		_slide.X = 0.0;
		System.Windows.Media.Color a = AccentToneColor(0.34);
		_root.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, a.R, a.G, a.B));
		ApplyBackdrop();
		WindowUtil.ForceForeground(this);
	}

	public void QaCaptureScreen(string outPath)
	{
		int x = (int)Math.Round(base.Left);
		int y = (int)Math.Round(base.Top);
		int w = (int)Math.Round(base.Width);
		int h = (int)Math.Round(base.Height);
		using Bitmap bmp = new Bitmap(w, h);
		using (Graphics g = Graphics.FromImage(bmp))
		{
			g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
		}
		bmp.Save(outPath, ImageFormat.Png);
		Logger.Log("QaCaptureScreen -> " + outPath);
	}

	private void Build()
	{
		Grid grid = new Grid
		{
			Margin = new Thickness(0.0, 32.0, 0.0, 28.0)
		};
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		TextBlock header = new TextBlock
		{
			Text = "Settings",
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 34.0,
			Margin = new Thickness(24.0, 0.0, 24.0, 16.0)
		};
		Grid.SetRow(header, 0);
		grid.Children.Add(header);
		string ctx = ForegroundContext.Classify();
		StackPanel links = new StackPanel();
		links.Children.Add(Link("Desktop", delegate
		{
			Act(ShowDesktop);
		}, ctx == "Desktop"));
		links.Children.Add(Link("Control Panel", delegate
		{
			Act(delegate
			{
				Launch("control");
			});
		}, ctx == "Control Panel"));
		links.Children.Add(Link("Personalization", delegate
		{
			Act(delegate
			{
				PersonalizeRequested?.Invoke();
			});
		}, ctx == "Personalization"));
		links.Children.Add(Link("PC info", delegate
		{
			Act(delegate
			{
				Launch("ms-settings:about");
			});
		}, ctx == "PC info"));
		links.Children.Add(Link("Help", delegate
		{
			Act(delegate
			{
				Launch("https://support.microsoft.com/windows");
			});
		}, ctx == "Help"));
		Grid.SetRow(links, 1);
		grid.Children.Add(links);
		System.Windows.Controls.Button change = Link("Change PC settings", delegate
		{
			Act(delegate
			{
				ChangePcSettingsRequested?.Invoke();
			});
		});
		change.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
		change.Padding = new Thickness(10.0, 8.0, 10.0, 8.0);
		change.Margin = new Thickness(0.0, 18.0, 14.0, 0.0);
		change.FontSize = 14.0;
		Grid.SetRow(change, 3);
		grid.Children.Add(change);
		_root.Child = grid;
	}

	[DllImport("user32.dll")]
	private static extern nint GetDesktopWindow();

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint flags);

	[DllImport("dxva2.dll")]
	private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint h, out uint n);

	[DllImport("dxva2.dll")]
	private static extern bool GetPhysicalMonitorsFromHMONITOR(nint h, uint n, [Out] PHYSICAL_MONITOR[] arr);

	[DllImport("dxva2.dll")]
	private static extern bool GetMonitorBrightness(nint h, out uint min, out uint cur, out uint max);

	[DllImport("dxva2.dll")]
	private static extern bool DestroyPhysicalMonitors(uint n, [In] PHYSICAL_MONITOR[] arr);

	private static string BrightnessLabel()
	{
		try
		{
			nint hmon = MonitorFromWindow(GetDesktopWindow(), 2u);
			if (hmon == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out var n) || n == 0)
			{
				return "Unavailable";
			}
			PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[n];
			if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, arr))
			{
				return "Unavailable";
			}
			try
			{
				if (GetMonitorBrightness(arr[0].h, out var _, out var cur, out var max) && max != 0)
				{
					return Math.Round((double)cur * 100.0 / (double)max).ToString();
				}
				return "Unavailable";
			}
			finally
			{
				DestroyPhysicalMonitors(n, arr);
			}
		}
		catch
		{
			return "Unavailable";
		}
	}

	private static void ShowDesktop()
	{
		try
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
			Type t = Type.GetTypeFromProgID("Shell.Application");
			if (t != null)
			{
				dynamic shell = Activator.CreateInstance(t);
				shell.MinimizeAll();
			}
		}
		catch (Exception ex)
		{
			Logger.Log("ShowDesktop: " + ex.Message);
		}
	}

	private static System.Windows.Controls.Button Link(string text, Action onClick, bool dim = false)
	{
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = text,
			Foreground = (dim ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, byte.MaxValue, byte.MaxValue, byte.MaxValue)) : System.Windows.Media.Brushes.White),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 16.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Padding = new Thickness(24.0, 13.0, 24.0, 13.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Template = LinkTemplate()
		};
		b.Click += delegate
		{
			onClick();
		};
		return b;
	}

	private static ControlTemplate LinkTemplate()
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
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(12, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		Trigger press = new Trigger
		{
			Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty,
			Value = true
		};
		press.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(press);
		return t;
	}

	private static QuickAction? CatalogById(string id)
	{
		QuickAction[] catalog = Catalog;
		foreach (QuickAction a in catalog)
		{
			if (a.Id == id)
			{
				return a;
			}
		}
		return null;
	}

	private static void EnsureSeeded(AppSettings s)
	{
		if (!s.CharmQuickCustomised)
		{
			s.CharmQuickOrder = Catalog.Select((QuickAction quickAction) => quickAction.Id).ToList();
			s.CharmQuickHidden = (from quickAction in Catalog
				where quickAction.DefaultHidden
				select quickAction.Id).ToList();
			s.CharmQuickCustomised = true;
			return;
		}
		QuickAction[] catalog = Catalog;
		foreach (QuickAction a in catalog)
		{
			if (!s.CharmQuickOrder.Contains(a.Id))
			{
				s.CharmQuickOrder.Add(a.Id);
				if (a.DefaultHidden && !s.CharmQuickHidden.Contains(a.Id))
				{
					s.CharmQuickHidden.Add(a.Id);
				}
			}
		}
		HashSet<string> ids = new HashSet<string>(Catalog.Select((QuickAction quickAction) => quickAction.Id));
		s.CharmQuickOrder.RemoveAll((string id) => !ids.Contains(id));
		s.CharmQuickHidden.RemoveAll((string id) => !ids.Contains(id));
	}

	private static (List<QuickAction> shown, List<QuickAction> hidden) ResolveCatalog(AppSettings s)
	{
		IEnumerable<QuickAction> seq;
		Func<QuickAction, bool> hidden;
		if (!s.CharmQuickCustomised)
		{
			seq = Catalog;
			hidden = (QuickAction a) => a.DefaultHidden;
		}
		else
		{
			seq = from a in Catalog
				orderby Idx(a.Id), Array.IndexOf(Catalog, a)
				select a;
			hidden = (QuickAction a) => s.CharmQuickHidden.Contains(a.Id) || (!s.CharmQuickOrder.Contains(a.Id) && a.DefaultHidden);
		}
		List<QuickAction> list = seq.ToList();
		return (shown: list.Where((QuickAction a) => !hidden(a)).ToList(), hidden: list.Where(hidden).ToList());
		int Idx(string id)
		{
			int i = s.CharmQuickOrder.IndexOf(id);
			return (i < 0) ? int.MaxValue : i;
		}
	}

	private void RenderCatalogTile(Grid host, int row, int col, QuickAction a, string brLabel)
	{
		switch (a.Id)
		{
		case "network":
		{
			(ImageSource, string) net = NetState();
			(TextBlock, TextBlock, System.Windows.Controls.Button, System.Windows.Controls.Image) t3 = AddQuick(host, row, col, "", net.Item2, OnNetwork, textGlyph: false, net.Item1);
			_netImage = t3.Item4;
			_netLabelTb = t3.Item2;
			break;
		}
		case "volume":
		{
			(string, string) vol = VolState();
			(_volGlyphTb, _volLabelTb, _volTile, _) = AddQuick(host, row, col, vol.Item1, vol.Item2, OnVolume);
			break;
		}
		case "brightness":
		{
			(TextBlock, TextBlock, System.Windows.Controls.Button, System.Windows.Controls.Image) t4 = AddQuick(host, row, col, G(59142), brLabel, OnBrightness);
			_brightLabelTb = t4.Item2;
			_brightTile = t4.Item3;
			_brightTile.Opacity = ((brLabel == "Unavailable") ? 0.5 : 1.0);
			break;
		}
		case "notifications":
			AddQuick(host, row, col, G(59367), "Notifications", delegate
			{
				Act(delegate
				{
					Launch("ms-settings:notifications");
				});
			});
			break;
		case "power":
			_powerTile = AddQuick(host, row, col, G(59368), "Power", OnPower).Tile;
			break;
		case "keyboard":
			AddQuick(host, row, col, InputLanguage(), "Keyboard", delegate
			{
				Act(delegate
				{
					Launch("osk.exe");
				});
			}, textGlyph: true);
			break;
		case "airplane":
		{
			(TextBlock, TextBlock, System.Windows.Controls.Button, System.Windows.Controls.Image) t2 = AddQuick(host, row, col, Gl(59145), "Airplane\nmode", ToggleAirplaneTile);
			if (t2.Item1 != null)
			{
				_extToggles.Add((t2.Item1, () => _extAirOn));
			}
			break;
		}
		case "bluetooth":
		{
			(TextBlock, TextBlock, System.Windows.Controls.Button, System.Windows.Controls.Image) t = AddQuick(host, row, col, Gl(59138), "Bluetooth", ToggleBluetoothTile);
			if (t.Item1 != null)
			{
				_extToggles.Add((t.Item1, () => _extBtOn));
			}
			break;
		}
		case "nightlight":
			AddQuick(host, row, col, Gl(59142), "Night light", delegate
			{
				Act(delegate
				{
					Launch("ms-settings:nightlight");
				});
			});
			break;
		case "vpn":
			AddQuick(host, row, col, Gl(59455), "VPN", delegate
			{
				Act(delegate
				{
					Launch("ms-settings:network-vpn");
				});
			});
			break;
		case "batterysaver":
			AddQuick(host, row, col, Gl(60352), "Battery\nsaver", delegate
			{
				Act(delegate
				{
					Launch("ms-settings:batterysaver");
				});
			});
			break;
		case "project":
			AddQuick(host, row, col, Gl(59380), "Project", delegate
			{
				Act(delegate
				{
					Keystroke.Chord(91, 80);
				});
			});
			break;
		case "connect":
			AddQuick(host, row, col, Gl(59139), "Connect", delegate
			{
				Act(delegate
				{
					Keystroke.Chord(91, 75);
				});
			});
			break;
		case "hotspot":
			AddQuick(host, row, col, Gl(59643), "Mobile\nhotspot", delegate
			{
				Act(delegate
				{
					Launch("ms-settings:network-mobilehotspot");
				});
			});
			break;
		case "focusassist":
			AddQuick(host, row, col, Gl(60032), "Focus\nassist", delegate
			{
				Act(delegate
				{
					Launch("ms-settings:quiethours");
				});
			});
			break;
		}
	}

	private (TextBlock? Glyph, TextBlock Label, System.Windows.Controls.Button Tile, System.Windows.Controls.Image? Img) AddQuick(Grid host, int row, int col, string glyph, string label, Action onClick, bool textGlyph = false, ImageSource? image = null)
	{
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(2.0)
		};
		TextBlock glyphTb = null;
		System.Windows.Controls.Image img = null;
		FrameworkElement icon;
		if (image != null)
		{
			img = new System.Windows.Controls.Image
			{
				Source = image,
				Width = _bmpSize,
				Height = _bmpSize,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
			RenderOptions.SetBitmapScalingMode((DependencyObject)(object)img, BitmapScalingMode.HighQuality);
			icon = new Grid
			{
				Height = _iconH,
				Children = { (UIElement)img }
			};
		}
		else
		{
			glyphTb = new TextBlock
			{
				Text = glyph,
				FontFamily = new System.Windows.Media.FontFamily(textGlyph ? "Segoe UI Semibold" : "Segoe MDL2 Assets"),
				FontSize = (textGlyph ? _textEm : _glyphEm),
				Height = _iconH,
				Foreground = System.Windows.Media.Brushes.White,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center
			};
			icon = glyphTb;
		}
		TextBlock labelTb = new TextBlock
		{
			Text = label,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = _labelEm,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 208, 208)),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			Margin = new Thickness(0.0, 5.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 92.0
		};
		panel.Children.Add(icon);
		panel.Children.Add(labelTb);
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = panel,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Height = _tileH,
			Template = HoverButtonTemplate()
		};
		b.Click += delegate
		{
			onClick();
		};
		Grid.SetRow(b, row);
		Grid.SetColumn(b, col);
		host.Children.Add(b);
		return (Glyph: glyphTb, Label: labelTb, Tile: b, Img: img);
	}

	private static string Gl(int cp)
	{
		return char.ConvertFromUtf32(cp);
	}

	private FrameworkElement BuildExtendedTiles(int curBr, List<QuickAction> actions, string brLabel)
	{
		StackPanel wrap = new StackPanel
		{
			Visibility = Visibility.Collapsed
		};
		if (actions.Count > 0)
		{
			Grid g = new Grid();
			for (int c = 0; c < 3; c++)
			{
				g.ColumnDefinitions.Add(new ColumnDefinition());
			}
			int rows = Math.Max(1, (actions.Count + 2) / 3);
			for (int r = 0; r < rows; r++)
			{
				g.RowDefinitions.Add(new RowDefinition());
			}
			for (int i = 0; i < actions.Count; i++)
			{
				RenderCatalogTile(g, i / 3, i % 3, actions[i], brLabel);
			}
			wrap.Children.Add(g);
		}
		if (curBr >= 0)
		{
			Grid brRow = new Grid
			{
				Margin = new Thickness(2.0, 12.0, 6.0, 2.0)
			};
			brRow.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			brRow.ColumnDefinitions.Add(new ColumnDefinition());
			TextBlock brGlyph = new TextBlock
			{
				Text = G(59142),
				FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
				FontSize = 18.0,
				Foreground = System.Windows.Media.Brushes.White,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(4.0, 0.0, 12.0, 0.0)
			};
			Slider brSlider = new Slider
			{
				Minimum = 0.0,
				Maximum = 100.0,
				Value = curBr,
				VerticalAlignment = VerticalAlignment.Center
			};
			brSlider.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.SliderDark");   // pattern 19, dark surface
			brSlider.ValueChanged += delegate(object _, RoutedPropertyChangedEventArgs<double> e)
			{
				int num = (int)Math.Round(e.NewValue);
				MonitorBrightness.SetThrottled(num);
				OsdService.ShowBrightness(num);
			};
			Grid.SetColumn(brGlyph, 0);
			Grid.SetColumn(brSlider, 1);
			brRow.Children.Add(brGlyph);
			brRow.Children.Add(brSlider);
			wrap.Children.Add(brRow);
		}
		return wrap;
	}

	private FrameworkElement MoreActionsToggle(FrameworkElement extended)
	{
		SolidColorBrush dim = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 208, 208));
		TextBlock text = new TextBlock
		{
			Text = "More actions",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Foreground = dim,
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBlock chevron = new TextBlock
		{
			Text = Gl(59149),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 12.0,
			Foreground = dim,
			Margin = new Thickness(6.0, 1.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		};
		row.Children.Add(text);
		row.Children.Add(chevron);
		System.Windows.Controls.Button btn = new System.Windows.Controls.Button
		{
			Content = row,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Margin = new Thickness(0.0, 10.0, 0.0, 2.0),
			Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
			Template = HoverButtonTemplate()
		};
		btn.Click += delegate
		{
			bool flag = extended.Visibility != Visibility.Visible;
			extended.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
			chevron.Text = Gl(flag ? 59150 : 59149);
			if (flag)
			{
				RefreshExtStates();
			}
		};
		return btn;
	}

	private static bool IsHidden(QuickAction a, AppSettings s)
	{
		return s.CharmQuickCustomised ? s.CharmQuickHidden.Contains(a.Id) : a.DefaultHidden;
	}

	private static List<QuickAction> OrderedCatalog(AppSettings s)
	{
		if (!s.CharmQuickCustomised)
		{
			return Catalog.ToList();
		}
		return (from a in Catalog
			orderby Idx(a.Id), Array.IndexOf(Catalog, a)
			select a).ToList();
		int Idx(string id)
		{
			int i = s.CharmQuickOrder.IndexOf(id);
			return (i < 0) ? int.MaxValue : i;
		}
	}

	private static void MoveInList(List<string> list, string id, int delta)
	{
		int i = list.IndexOf(id);
		if (i >= 0)
		{
			int j = i + delta;
			if (j >= 0 && j < list.Count)
			{
				int index = i;
				int index2 = j;
				string value = list[j];
				string value2 = list[i];
				list[index] = value;
				list[index2] = value2;
			}
		}
	}

	private FrameworkElement CustomiseEntryRow()
	{
		SolidColorBrush dim = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 208, 208));
		TextBlock glyph = new TextBlock
		{
			Text = Gl(59151),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 13.0,
			Foreground = dim,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
		};
		TextBlock text = new TextBlock
		{
			Text = "Customize",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Foreground = dim,
			VerticalAlignment = VerticalAlignment.Center
		};
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		row.Children.Add(glyph);
		row.Children.Add(text);
		System.Windows.Controls.Button btn = new System.Windows.Controls.Button
		{
			Content = row,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
			Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
			Template = HoverButtonTemplate()
		};
		btn.Click += delegate
		{
			_customising = true;
			Build();
		};
		return btn;
	}

	private FrameworkElement BuildCustomiseEditor(AppSettings s, List<QuickAction> shown, List<QuickAction> hidden)
	{
		SolidColorBrush dim = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 208, 208));
		SolidColorBrush faint = new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		StackPanel root = new StackPanel();
		Grid head = new Grid
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		head.ColumnDefinitions.Add(new ColumnDefinition());
		head.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		TextBlock title = new TextBlock
		{
			Text = "Customize",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 20.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center
		};
		SolidColorBrush brightAccent = new SolidColorBrush(AccentToneColor(0.52));
		System.Windows.Controls.Button done = new System.Windows.Controls.Button
		{
			Content = "Done",
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Background = brightAccent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Padding = new Thickness(14.0, 5.0, 14.0, 5.0),
			Template = HoverButtonTemplate()
		};
		done.Click += delegate
		{
			_customising = false;
			Build();
		};
		Grid.SetColumn(done, 1);
		head.Children.Add(title);
		head.Children.Add(done);
		root.Children.Add(head);
		List<QuickAction> ordered = OrderedCatalog(s);
		for (int i = 0; i < ordered.Count; i++)
		{
			QuickAction a = ordered[i];
			bool isHidden = IsHidden(a, s);
			bool first = i == 0;
			bool last = i == ordered.Count - 1;
			root.Children.Add(BuildCustomiseRow(a, isHidden, first, last, dim, faint));
		}
		StackPanel densRow = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
		};
		densRow.Children.Add(new TextBlock
		{
			Text = "Density",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Foreground = dim,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		});
		densRow.Children.Add(SegPill("Comfortable", !s.CharmCompact, delegate
		{
			SetCompact(compact: false);
		}));
		densRow.Children.Add(SegPill("Compact", s.CharmCompact, delegate
		{
			SetCompact(compact: true);
		}));
		root.Children.Add(densRow);
		System.Windows.Controls.Button accentLink = new System.Windows.Controls.Button
		{
			Content = "Accent colour…",
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Padding = new Thickness(0.0, 6.0, 0.0, 0.0),
			Template = HoverButtonTemplate()
		};
		accentLink.Click += delegate
		{
			Act(delegate
			{
				PersonalizeRequested?.Invoke();
			});
		};
		root.Children.Add(accentLink);
		StackPanel bdRow = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		bdRow.Children.Add(new TextBlock
		{
			Text = "Backdrop",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Foreground = dim,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		});
		string backdrop = (s.CharmBlur ? "acrylic" : (s.CharmTransparent ? "transparent" : "solid"));
		bdRow.Children.Add(SegPill("Solid", backdrop == "solid", delegate
		{
			SetBackdrop(transparent: false, blur: false);
		}));
		bdRow.Children.Add(SegPill("Transparent", backdrop == "transparent", delegate
		{
			SetBackdrop(transparent: true, blur: false);
		}));
		bdRow.Children.Add(SegPill("Acrylic", backdrop == "acrylic", delegate
		{
			SetBackdrop(transparent: true, blur: true);
		}));
		root.Children.Add(bdRow);
		root.Children.Add(new TextBlock
		{
			Text = "Drag a row to reorder (or use ▲▼), and tap the box to show or hide.",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Foreground = faint,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
		});
		return root;
	}

	private FrameworkElement BuildCustomiseRow(QuickAction a, bool isHidden, bool first, bool last, System.Windows.Media.Brush dim, System.Windows.Media.Brush faint)
	{
		Grid g = new Grid
		{
			Margin = new Thickness(0.0, 1.0, 0.0, 1.0),
			Opacity = (isHidden ? 0.55 : 1.0),
			Background = System.Windows.Media.Brushes.Transparent,
			Cursor = System.Windows.Input.Cursors.SizeAll,
			Tag = a.Id,
			AllowDrop = true
		};
		g.PreviewMouseLeftButtonDown += OnCharmRowDown;
		g.PreviewMouseMove += OnCharmRowMove;
		g.DragOver += OnCharmRowDragOver;
		g.Drop += OnCharmRowDrop;
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		g.ColumnDefinitions.Add(new ColumnDefinition());
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		g.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		TextBlock grip = new TextBlock
		{
			Text = Gl(59247),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 14.0,
			Foreground = faint,
			VerticalAlignment = VerticalAlignment.Center,
			Width = 22.0,
			TextAlignment = TextAlignment.Center
		};
		Grid.SetColumn(grip, 0);
		bool textGlyph = a.Kind == QAKind.TextGlyph;
		TextBlock icon = new TextBlock
		{
			Text = (textGlyph ? InputLanguage() : Gl(a.Glyph)),
			FontFamily = new System.Windows.Media.FontFamily(textGlyph ? "Segoe UI Semibold" : "Segoe MDL2 Assets"),
			FontSize = (textGlyph ? 13 : 18),
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			Width = 30.0,
			TextAlignment = TextAlignment.Center
		};
		Grid.SetColumn(icon, 1);
		TextBlock label = new TextBlock
		{
			Text = a.Label.Replace("\n", " "),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8.0, 0.0, 8.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		Grid.SetColumn(label, 2);
		System.Windows.Controls.Button showBtn = EditorGlyphButton(isHidden ? 59193 : 59198, System.Windows.Media.Brushes.White, enabled: true, delegate
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				EnsureSeeded(x);
				if (isHidden)
				{
					x.CharmQuickHidden.Remove(a.Id);
				}
				else if (!x.CharmQuickHidden.Contains(a.Id))
				{
					x.CharmQuickHidden.Add(a.Id);
				}
			});
		});
		showBtn.ToolTip = (isHidden ? "Show" : "Hide");
		Grid.SetColumn(showBtn, 3);
		System.Windows.Controls.Button upBtn = EditorGlyphButton(59150, first ? faint : System.Windows.Media.Brushes.White, !first, delegate
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				EnsureSeeded(x);
				MoveInList(x.CharmQuickOrder, a.Id, -1);
			});
		});
		Grid.SetColumn(upBtn, 4);
		System.Windows.Controls.Button downBtn = EditorGlyphButton(59149, last ? faint : System.Windows.Media.Brushes.White, !last, delegate
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				EnsureSeeded(x);
				MoveInList(x.CharmQuickOrder, a.Id, 1);
			});
		});
		Grid.SetColumn(downBtn, 5);
		g.Children.Add(grip);
		g.Children.Add(icon);
		g.Children.Add(label);
		g.Children.Add(showBtn);
		g.Children.Add(upBtn);
		g.Children.Add(downBtn);
		return g;
	}

	private void OnCharmRowDown(object sender, MouseButtonEventArgs e)
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		if (IsWithinButton(e.OriginalSource))
		{
			_charmDragId = null;
			return;
		}
		_charmDragStart = e.GetPosition(null);
		_charmDragId = (sender as FrameworkElement)?.Tag as string;
	}

	private void OnCharmRowMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Expected O, but got Unknown
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			_charmDragId = null;
		}
		else
		{
			if (_charmDragId == null)
			{
				return;
			}
			System.Windows.Point p = e.GetPosition(null);
			if (!(Math.Abs(p.X - _charmDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) || !(Math.Abs(p.Y - _charmDragStart.Y) < SystemParameters.MinimumVerticalDragDistance))
			{
				string id = _charmDragId;
				_charmDragId = null;
				try
				{
					DragDrop.DoDragDrop((DependencyObject)sender, new System.Windows.DataObject("Win81CharmRow", id), System.Windows.DragDropEffects.Move);
				}
				catch (Exception ex)
				{
					Logger.Log("charm row drag: " + ex.Message);
				}
			}
		}
	}

	private void OnCharmRowDragOver(object sender, System.Windows.DragEventArgs e)
	{
		e.Effects = (e.Data.GetDataPresent("Win81CharmRow") ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None);
		e.Handled = true;
	}

	private void OnCharmRowDrop(object sender, System.Windows.DragEventArgs e)
	{
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		if (!e.Data.GetDataPresent("Win81CharmRow"))
		{
			return;
		}
		e.Handled = true;
		string dragged = e.Data.GetData("Win81CharmRow") as string;
		string target = (sender as FrameworkElement)?.Tag as string;
		if (dragged == null || target == null || dragged == target)
		{
			return;
		}
		int num;
		if (sender is FrameworkElement { ActualHeight: >0.0 } fe)
		{
			System.Windows.Point position = e.GetPosition(fe);
			num = ((position.Y > fe.ActualHeight / 2.0) ? 1 : 0);
		}
		else
		{
			num = 0;
		}
		bool after = (byte)num != 0;
		SettingsStore.Update(delegate(AppSettings x)
		{
			EnsureSeeded(x);
			x.CharmQuickOrder.Remove(dragged);
			int num2 = x.CharmQuickOrder.IndexOf(target);
			if (num2 < 0)
			{
				num2 = x.CharmQuickOrder.Count;
			}
			else if (after)
			{
				num2++;
			}
			num2 = Math.Max(0, Math.Min(num2, x.CharmQuickOrder.Count));
			x.CharmQuickOrder.Insert(num2, dragged);
		});
		Build();
	}

	private static bool IsWithinButton(object? src)
	{
		for (DependencyObject d = (DependencyObject)((src is DependencyObject) ? src : null); d != null; d = ((d is Visual || d is Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)))
		{
			if (d is System.Windows.Controls.Button)
			{
				return true;
			}
		}
		return false;
	}

	private System.Windows.Controls.Button EditorGlyphButton(int glyph, System.Windows.Media.Brush color, bool enabled, Action commit)
	{
		TextBlock tb = new TextBlock
		{
			Text = Gl(glyph),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 15.0,
			Foreground = color,
			VerticalAlignment = VerticalAlignment.Center,
			TextAlignment = TextAlignment.Center,
			Width = 30.0
		};
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = tb,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Height = 34.0,
			IsEnabled = enabled,
			Template = HoverButtonTemplate()
		};
		b.Click += delegate
		{
			commit();
			Build();
		};
		return b;
	}

	private System.Windows.Controls.Button SegPill(string text, bool selected, Action onClick)
	{
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = text,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			FontWeight = (selected ? FontWeights.SemiBold : FontWeights.Normal),
			Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(selected ? 89 : 26), byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Padding = new Thickness(12.0, 4.0, 12.0, 4.0),
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
			Template = HoverButtonTemplate()
		};
		b.Click += delegate
		{
			onClick();
		};
		return b;
	}

	private void SetCompact(bool compact)
	{
		SettingsStore.Update(delegate(AppSettings x)
		{
			x.CharmCompact = compact;
		});
		Build();
	}

	private void SetBackdrop(bool transparent, bool blur)
	{
		SettingsStore.Update(delegate(AppSettings x)
		{
			x.CharmTransparent = transparent;
			x.CharmBlur = blur;
		});
		_root.Background = PaneBg();
		ApplyBackdrop();
		Build();
	}

	private async void ToggleAirplaneTile()
	{
		_toggling = true;
		try
		{
			if (!(await RadioQuick.ToggleAirplane()))
			{
				Act(delegate
				{
					Launch("ms-settings:network-airplanemode");
				});
			}
			else
			{
				await RefreshExtStates();
			}
		}
		finally
		{
			_toggling = false;
		}
	}

	private async void ToggleBluetoothTile()
	{
		_toggling = true;
		try
		{
			if (!(await RadioQuick.ToggleBluetooth()))
			{
				Act(delegate
				{
					Launch("ms-settings:bluetooth");
				});
			}
			else
			{
				await RefreshExtStates();
			}
		}
		finally
		{
			_toggling = false;
		}
	}

	private async Task RefreshExtStates()
	{
		(bool airplaneOn, bool bluetoothOn) tuple = await RadioQuick.ReadStates();
		bool air = tuple.airplaneOn;
		bool bt = tuple.bluetoothOn;
		_extAirOn = air;
		_extBtOn = bt;
		try
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				SolidColorBrush solidColorBrush = new SolidColorBrush(StartAccent.Color());
				foreach (var extToggle in _extToggles)
				{
					TextBlock item = extToggle.glyph;
					Func<bool> item2 = extToggle.on;
					bool flag = false;
					try
					{
						flag = item2();
					}
					catch
					{
					}
					item.Foreground = (flag ? solidColorBrush : System.Windows.Media.Brushes.White);
				}
			});
		}
		catch
		{
		}
	}

	private void OnNetwork()
	{
		Act(delegate
		{
			Launch("ms-settings:network-status");
		});
	}

	private void OnVolume()
	{
		Border content = new Border
		{
			Background = _root.Background,
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(85, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(14.0, 12.0, 14.0, 12.0),
			Width = 240.0
		};
		StackPanel stack = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		TextBlock muteGlyph = new TextBlock
		{
			Text = G(_audio.GetMute() ? 59215 : 59797),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 20.0,
			Foreground = System.Windows.Media.Brushes.White
		};
		System.Windows.Controls.Button mute = new System.Windows.Controls.Button
		{
			Content = muteGlyph,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Template = HoverButtonTemplate(),
			Width = 34.0,
			Height = 34.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		Slider slider = new Slider
		{
			Minimum = 0.0,
			Maximum = 100.0,
			Value = Math.Round(_audio.GetVolume() * 100f),
			Width = 170.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0)
		};
		slider.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.SliderDark");   // pattern 19, dark surface
		slider.ValueChanged += delegate(object _, RoutedPropertyChangedEventArgs<double> e)
		{
			_audio.SetVolume((float)(e.NewValue / 100.0));
			OsdService.ShowVolume((int)Math.Round(e.NewValue), _audio.GetMute());
		};
		mute.Click += delegate
		{
			bool flag = !_audio.GetMute();
			_audio.SetMute(flag);
			muteGlyph.Text = G(flag ? 59215 : 59797);
			OsdService.ShowVolume((int)Math.Round(_audio.GetVolume() * 100f), flag);
		};
		stack.Children.Add(mute);
		stack.Children.Add(slider);
		content.Child = stack;
		ShowFlyout(content, _volTile);
	}

	private void OnBrightness()
	{
		int cur = MonitorBrightness.Get();
		if (cur < 0)
		{
			Act(delegate
			{
				Launch("ms-settings:display");
			});
			return;
		}
		Border content = new Border
		{
			Background = _root.Background,
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(85, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(14.0, 12.0, 14.0, 12.0),
			Width = 240.0
		};
		StackPanel stack = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		stack.Children.Add(new TextBlock
		{
			Text = G(59142),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 20.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(4.0, 0.0, 0.0, 0.0)
		});
		Slider slider = new Slider
		{
			Minimum = 0.0,
			Maximum = 100.0,
			Value = cur,
			Width = 170.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0)
		};
		slider.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.SliderDark");   // pattern 19, dark surface
		slider.ValueChanged += delegate(object _, RoutedPropertyChangedEventArgs<double> e)
		{
			int num = (int)Math.Round(e.NewValue);
			OsdService.ShowBrightness(num);
			MonitorBrightness.SetThrottled(num);
		};
		stack.Children.Add(slider);
		content.Child = stack;
		ShowFlyout(content, _brightTile);
	}

	private void OnPower()
	{
		StackPanel panel = new StackPanel
		{
			Background = _root.Background
		};
		Border wrap = new Border
		{
			Background = _root.Background,
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(85, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Child = panel,
			MinWidth = 180.0
		};
		panel.Children.Add(PowerItem("Sleep", delegate
		{
			Act(Sleep);
		}));
		panel.Children.Add(PowerItem("Shut down", delegate
		{
			Act(delegate
			{
				Launch("shutdown.exe", "/s /t 0");
			});
		}));
		panel.Children.Add(PowerItem("Update and restart", delegate
		{
			Act(delegate
			{
				Launch("shutdown.exe", "/r /t 0");
			});
		}));
		ShowFlyout(wrap, _powerTile, PopupAnimation.Slide);
	}

	private System.Windows.Controls.Button PowerItem(string text, Action onClick)
	{
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = text,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Padding = new Thickness(16.0, 10.0, 16.0, 10.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Template = HoverButtonTemplate()
		};
		b.Click += delegate
		{
			onClick();
		};
		return b;
	}

	private void ShowFlyout(UIElement content, UIElement? anchor = null, PopupAnimation anim = PopupAnimation.Fade)
	{
		if (_flyout == null)
		{
			_flyout = new Popup
			{
				StaysOpen = false,
				AllowsTransparency = true
			};
		}
		_flyout.PlacementTarget = anchor ?? _root;
		_flyout.Placement = ((anchor == null) ? PlacementMode.Center : PlacementMode.Top);
		_flyout.PopupAnimation = anim;
		_flyout.HorizontalOffset = 0.0;
		_flyout.VerticalOffset = ((anchor != null) ? (-6) : 0);
		_flyout.Child = content;
		_flyout.IsOpen = true;
	}

	[DllImport("powrprof.dll", SetLastError = true)]
	private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvents);

	private static void Sleep()
	{
		SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvents: false);
	}

	private static void Launch(string cmd, string? args = null)
	{
		ProcessStartInfo psi = ((args == null) ? new ProcessStartInfo(cmd)
		{
			UseShellExecute = true
		} : new ProcessStartInfo(cmd, args)
		{
			UseShellExecute = true
		});
		Process.Start(psi);
	}

	private static ControlTemplate HoverButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
		border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		border.Name = "bd";
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		border.AppendChild(cp);
		t.VisualTree = border;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(18, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		Trigger press = new Trigger
		{
			Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty,
			Value = true
		};
		press.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(36, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(press);
		return t;
	}
}
