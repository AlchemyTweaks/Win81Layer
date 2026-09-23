using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Win81Layer;

public sealed class MetroTilePresenter : Grid
{
	public static readonly DependencyProperty VisualProperty = DependencyProperty.Register(
		nameof(Visual), typeof(object), typeof(MetroTilePresenter),
		new PropertyMetadata(null, OnVisualChanged));

	public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(
		nameof(MotionEnabled), typeof(bool), typeof(MetroTilePresenter),
		new PropertyMetadata(false, OnMotionEnabledChanged));

	private Border _front;
	private Border _back;
	private readonly Border _tint;
	private readonly Rectangle _particles;
	private readonly Image _foreground;
	private readonly TranslateTransform _particleShift = new TranslateTransform();
	private ScrollViewer? _scrollViewer;
	private bool _motionRunning;
	private long _transitionVersion;
	private WeatherTileArt.Category _category;

	public object? Visual
	{
		get => GetValue(VisualProperty);
		set => SetValue(VisualProperty, value);
	}

	public bool MotionEnabled
	{
		get => (bool)GetValue(MotionEnabledProperty);
		set => SetValue(MotionEnabledProperty, value);
	}

	public MetroTilePresenter()
	{
		ClipToBounds = true;
		IsHitTestVisible = false;
		SnapsToDevicePixels = true;

		_front = BackgroundLayer();
		_back = BackgroundLayer();
		_back.Opacity = 0;
		Children.Add(_back);
		Children.Add(_front);

		_tint = new Border { IsHitTestVisible = false };
		Children.Add(_tint);

		_particles = new Rectangle
		{
			Visibility = Visibility.Collapsed,
			IsHitTestVisible = false,
			RenderTransform = _particleShift,
			CacheMode = new BitmapCache(1.0)
		};
		Children.Add(_particles);

		_foreground = new Image
		{
			Stretch = Stretch.Fill,
			IsHitTestVisible = false,
			SnapsToDevicePixels = true
		};
		RenderOptions.SetBitmapScalingMode(_foreground, BitmapScalingMode.HighQuality);
		Children.Add(_foreground);

		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		IsVisibleChanged += delegate { UpdateMotionState(); };
		SizeChanged += delegate { UpdateMotionState(); };
	}

	private static Border BackgroundLayer()
	{
		ScaleTransform scale = new ScaleTransform(1.055, 1.055);
		TranslateTransform shift = new TranslateTransform();
		TransformGroup transform = new TransformGroup();
		transform.Children.Add(scale);
		transform.Children.Add(shift);
		return new Border
		{
			RenderTransformOrigin = new Point(0.5, 0.5),
			RenderTransform = transform,
			CacheMode = new BitmapCache(1.0),
			IsHitTestVisible = false
		};
	}

	private static void OnVisualChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
	{
		((MetroTilePresenter)target).ApplyVisual(args.NewValue as MetroTileVisual);
	}

	private static void OnMotionEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
	{
		((MetroTilePresenter)target).UpdateMotionState();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		_scrollViewer = FindParent<ScrollViewer>(this);
		if (_scrollViewer != null) _scrollViewer.ScrollChanged += OnScrollChanged;
		ApplyVisual(Visual as MetroTileVisual);
		UpdateMotionState();
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
		_scrollViewer = null;
		StopMotion();
	}

	private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		UpdateMotionState();
	}

	private void ApplyVisual(MetroTileVisual? visual)
	{
		if (visual == null)
		{
			_transitionVersion++;
			_front.Background = null;
			_back.Background = null;
			_foreground.Source = null;
			_particles.Visibility = Visibility.Collapsed;
			StopMotion();
			return;
		}
		// Updating text must not restart an unchanged background pan/crossfade.
		if (_front.Background is ImageBrush current && ReferenceEquals(current.ImageSource, visual.Background) && _category == visual.Category)
		{
			_foreground.Source = visual.Foreground;
			_tint.Background = new SolidColorBrush(visual.Tint);
			UpdateMotionState();
			return;
		}
		long transitionVersion = ++_transitionVersion;
		_category = visual.Category;
		if (_motionRunning) StopMotion();

		ImageBrush newBackground = WeatherTileArt.BackgroundBrush(visual.Background, visual.Size);
		bool hasPrevious = _front.Background != null;
		Border incoming = hasPrevious ? _back : _front;
		Border outgoing = hasPrevious ? _front : _back;
		incoming.BeginAnimation(OpacityProperty, null);
		outgoing.BeginAnimation(OpacityProperty, null);
		incoming.Background = newBackground;
		incoming.Opacity = hasPrevious ? 0 : 1;
		_front = incoming;
		_back = outgoing;

		_tint.Background = new SolidColorBrush(visual.Tint);
		_foreground.Source = visual.Foreground;
		ConfigureParticles(visual.Category);

		if (hasPrevious && CanRunMotion())
		{
			Duration duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(1, 220 * Motion.Factor)));
			CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
			DoubleAnimation fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
			DoubleAnimation fadeOut = new DoubleAnimation(outgoing.Opacity, 0, duration) { EasingFunction = ease };
			fadeOut.Completed += delegate
			{
				if (_transitionVersion != transitionVersion || ReferenceEquals(outgoing, _front)) return;
				outgoing.BeginAnimation(OpacityProperty, null);
				outgoing.Opacity = 0;
				outgoing.Background = null;
			};
			incoming.BeginAnimation(OpacityProperty, fadeIn);
			outgoing.BeginAnimation(OpacityProperty, fadeOut);
		}
		else
		{
			incoming.Opacity = 1;
			outgoing.Opacity = 0;
			outgoing.Background = null;
		}
		UpdateMotionState();
	}

	private void ConfigureParticles(WeatherTileArt.Category category)
	{
		bool rain = category is WeatherTileArt.Category.Rain or WeatherTileArt.Category.Thunder;
		bool snow = category == WeatherTileArt.Category.Snow;
		if (!rain && !snow)
		{
			_particles.Fill = null;
			_particles.Visibility = Visibility.Collapsed;
			return;
		}

		const double tile = 48;
		DrawingGroup group = new DrawingGroup();
		using (DrawingContext dc = group.Open())
		{
			if (rain)
			{
				Pen pen = new Pen(new SolidColorBrush(Color.FromArgb(140, 225, 240, 255)), 1.15)
				{
					StartLineCap = PenLineCap.Round,
					EndLineCap = PenLineCap.Round
				};
				pen.Freeze();
				foreach ((double x, double y) in new[] { (7d, 2d), (23d, 16d), (41d, 7d), (14d, 34d), (35d, 40d) })
					dc.DrawLine(pen, new Point(x, y), new Point(x - 3.2, y + 9.5));
			}
			else
			{
				SolidColorBrush flake = new SolidColorBrush(Color.FromArgb(175, 255, 255, 255));
				flake.Freeze();
				foreach ((double x, double y, double r) in new[] { (8d, 5d, 1.2), (27d, 12d, 1.7), (43d, 25d, 1.1), (16d, 36d, 1.5), (35d, 44d, 1.3) })
					dc.DrawEllipse(flake, null, new Point(x, y), r, r);
			}
		}
		group.Freeze();
		DrawingBrush brush = new DrawingBrush(group)
		{
			TileMode = TileMode.Tile,
			ViewportUnits = BrushMappingMode.Absolute,
			Viewport = new Rect(0, 0, tile, tile),
			Stretch = Stretch.None
		};
		brush.Freeze();
		_particles.Fill = brush;
		_particles.Opacity = rain ? 0.22 : 0.30;
		_particles.Visibility = Visibility.Visible;
	}

	private void UpdateMotionState()
	{
		bool shouldRun = CanRunMotion();
		if (shouldRun && !_motionRunning) StartMotion();
		else if (!shouldRun && _motionRunning) StopMotion();
	}

	private bool CanRunMotion()
	{
		MetroTileVisual? visual = Visual as MetroTileVisual;
		if (!MotionEnabled || visual == null || !visual.AnimateBackground || !IsLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
			return false;
		if (Motion.Mode is MotionMode.Off or MotionMode.Reduced) return false;
		// LiveTileService folds battery-saver state into MotionEnabled once per service tick. Avoid repeating the
		// power-status query for every tile whenever the Start viewport scrolls.
		return IsInsideViewport();
	}

	private bool IsInsideViewport()
	{
		if (_scrollViewer == null) return true;
		try
		{
			Rect tileBounds = TransformToAncestor(_scrollViewer).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
			Rect viewport = new Rect(0, 0, _scrollViewer.ViewportWidth, _scrollViewer.ViewportHeight);
			return tileBounds.IntersectsWith(viewport);
		}
		catch
		{
			return true;
		}
	}

	private void StartMotion()
	{
		_motionRunning = true;
		StopBackgroundAnimations(_back);
		TranslateTransform pan = PanOf(_front);
		double distance = Math.Max(2.5, Math.Min(6.0, ActualWidth * 0.018));
		pan.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-distance, distance, TimeSpan.FromSeconds(28))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever
		});

		MetroTileVisual? visual = Visual as MetroTileVisual;
		if (_particles.Visibility == Visibility.Visible && visual != null)
		{
			double seconds = visual.Category == WeatherTileArt.Category.Snow ? 4.6 : 1.15;
			_particleShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 48, TimeSpan.FromSeconds(seconds))
			{
				RepeatBehavior = RepeatBehavior.Forever
			});
		}
	}

	private void StopMotion()
	{
		_motionRunning = false;
		StopBackgroundAnimations(_front);
		StopBackgroundAnimations(_back);
		_particleShift.BeginAnimation(TranslateTransform.YProperty, null);
		_particleShift.Y = 0;
	}

	private static void StopBackgroundAnimations(Border layer)
	{
		TranslateTransform pan = PanOf(layer);
		pan.BeginAnimation(TranslateTransform.XProperty, null);
		pan.X = 0;
	}

	private static TranslateTransform PanOf(Border layer)
	{
		return (TranslateTransform)((TransformGroup)layer.RenderTransform).Children[1];
	}

	private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
	{
		DependencyObject? current = child;
		while (current != null)
		{
			if (current is T match) return match;
			current = VisualTreeHelper.GetParent(current);
		}
		return null;
	}
}
