using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Win81Layer;

// A native Button with a single drawing surface. The old XAML template expanded every app
// into a deep visual tree, which made the 150-300 item Apps view expensive to build and animate.
public sealed class AppListItem : Button, IDisposable
{
	private static readonly Typeface NameTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

	private static readonly Typeface NewTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

	private static readonly Brush WhiteBrush = FrozenBrush(Color.FromRgb(255, 255, 255));

	private static readonly Brush NewBrush = FrozenBrush(Color.FromRgb(108, 184, 255));

	private static readonly Pen FocusPen = FrozenPen(Color.FromArgb(102, 255, 255, 255), 1.0);

	private static readonly Pen CheckPen = FrozenPen(Color.FromRgb(255, 255, 255), 1.6);

	private static readonly ControlTemplate EmptyTemplate = new ControlTemplate(typeof(Button));

	private static readonly Geometry CheckGeometry = BuildCheckGeometry();

	private readonly AppEntry _entry;

	private Brush _hoverBrush;

	private Brush _accentBrush;

	private FormattedText? _nameText;

	private FormattedText? _newText;

	private double _textPixelsPerDip;

	public AppListItem(AppEntry entry, Brush hoverBrush, Brush accentBrush)
	{
		_entry = entry;
		_hoverBrush = hoverBrush;
		_accentBrush = accentBrush;
		DataContext = entry;
		Content = entry.Name;
		Width = 248.0;
		Height = AppsColumnsPanel.ItemHeight;
		Focusable = true;
		IsTabStop = true;
		Cursor = Cursors.Hand;
		FocusVisualStyle = null;
		OverridesDefaultStyle = true;
		Template = EmptyTemplate;
		SnapsToDevicePixels = true;
		UseLayoutRounding = true;
		RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
		AutomationProperties.SetName(this, entry.Name);
		ToolTip = entry.Name;

		entry.PropertyChanged += OnEntryPropertyChanged;
		MouseEnter += OnVisualStateChanged;
		MouseLeave += OnVisualStateChanged;
		PreviewMouseDown += OnVisualStateChanged;
		PreviewMouseUp += OnVisualStateChanged;
		GotKeyboardFocus += OnVisualStateChanged;
		LostKeyboardFocus += OnVisualStateChanged;
	}

	public void SetTheme(Brush hoverBrush, Brush accentBrush)
	{
		_hoverBrush = hoverBrush;
		_accentBrush = accentBrush;
		InvalidateVisual();
	}

	public void Dispose()
	{
		_entry.PropertyChanged -= OnEntryPropertyChanged;
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		Rect bounds = new Rect(0.0, 0.0, 248.0, AppsColumnsPanel.ItemHeight);
		if (_entry.IsSelected || IsMouseOver || IsKeyboardFocused || IsPressed)
		{
			drawingContext.DrawRectangle(_hoverBrush, IsKeyboardFocused ? FocusPen : null, bounds);
		}

		Rect iconTile = new Rect(6.0, (AppsColumnsPanel.ItemHeight - 32.0) / 2.0, 32.0, 32.0);
		drawingContext.DrawRectangle(_entry.TileBrush, null, iconTile);
		if (_entry.IsOverrideIcon && _entry.OverrideBrush != null)
		{
			drawingContext.DrawRectangle(_entry.OverrideBrush, null, iconTile);
		}
		if (_entry.Icon != null)
		{
			DrawIcon(drawingContext, _entry.Icon, iconTile, _entry.IsOverrideIcon ? 32.0 : 24.0);
		}
		else
		{
			// An app whose icon never resolved draws its initial instead of an empty slot.
			IconResolver.DrawLetter(drawingContext, iconTile, _entry.Name, IconResolver.AccentFor(_entry.Name));
		}

		double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		EnsureText(pixelsPerDip);
		if (_nameText != null)
		{
			// Sit the name slightly ABOVE centre so a wrapped 2nd line has room to drop below (per user request).
			double y = Math.Max(2.0, (AppsColumnsPanel.ItemHeight - _nameText.Height) * 0.5 - 3.0);
			drawingContext.DrawText(_nameText, new Point(48.0, y));
		}
		if (_entry.IsNew && _newText != null)
		{
			double y = Math.Max(2.0, (AppsColumnsPanel.ItemHeight - _newText.Height) * 0.5 - 3.0);
			drawingContext.DrawText(_newText, new Point(204.0, y));
		}
		if (_entry.IsSelected)
		{
			DrawSelectionCheck(drawingContext);
		}
	}

	private void EnsureText(double pixelsPerDip)
	{
		if (_nameText != null && Math.Abs(_textPixelsPerDip - pixelsPerDip) < 0.01)
		{
			return;
		}
		_textPixelsPerDip = pixelsPerDip;
		_nameText = new FormattedText(_entry.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
			NameTypeface, 14.0, WhiteBrush, pixelsPerDip)
		{
			MaxTextWidth = 150.0,
			MaxLineCount = 2,
			Trimming = TextTrimming.CharacterEllipsis
		};
		_newText = new FormattedText("new", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
			NewTypeface, 11.0, NewBrush, pixelsPerDip)
		{
			MaxTextWidth = 36.0,
			MaxLineCount = 1,
			Trimming = TextTrimming.None
		};
	}

	private static void DrawIcon(DrawingContext drawingContext, ImageSource? icon, Rect tile, double targetSize)
	{
		if (icon == null)
		{
			return;
		}
		double width = targetSize;
		double height = targetSize;
		try
		{
			double sourceWidth = icon.Width;
			double sourceHeight = icon.Height;
			if (sourceWidth > 0.0 && sourceHeight > 0.0)
			{
				double scale = Math.Min(targetSize / sourceWidth, targetSize / sourceHeight);
				width = Math.Max(1.0, sourceWidth * scale);
				height = Math.Max(1.0, sourceHeight * scale);
			}
		}
		catch
		{
		}
		Rect target = new Rect(tile.X + (tile.Width - width) * 0.5, tile.Y + (tile.Height - height) * 0.5, width, height);
		drawingContext.DrawImage(icon, target);
	}

	private void DrawSelectionCheck(DrawingContext drawingContext)
	{
		Rect check = new Rect(222.0, 3.0, 20.0, 20.0);
		drawingContext.DrawRectangle(_accentBrush, null, check);
		drawingContext.DrawGeometry(null, CheckPen, CheckGeometry);
	}

	private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		Dispatcher.BeginInvoke((Action)InvalidateVisual, System.Windows.Threading.DispatcherPriority.Render);
	}

	private void OnVisualStateChanged(object sender, EventArgs e)
	{
		InvalidateVisual();
	}

	private static Brush FrozenBrush(Color color)
	{
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private static Pen FrozenPen(Color color, double thickness)
	{
		Pen pen = new Pen(FrozenBrush(color), thickness);
		pen.Freeze();
		return pen;
	}

	private static Geometry BuildCheckGeometry()
	{
		StreamGeometry geometry = new StreamGeometry();
		using (StreamGeometryContext context = geometry.Open())
		{
			context.BeginFigure(new Point(227.0, 13.0), isFilled: false, isClosed: false);
			context.LineTo(new Point(231.0, 17.0), isStroked: true, isSmoothJoin: true);
			context.LineTo(new Point(238.0, 8.0), isStroked: true, isSmoothJoin: true);
		}
		geometry.Freeze();
		return geometry;
	}
}
