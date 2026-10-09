#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Win81Layer;

// The seasonal effect behind the Start tiles: falling snow or drifting bats. Each layer is one rectangle filled with
// a cached, tiled drawing that is moved by exactly one tile, so the motion loops seamlessly and only a transform is
// animated. It runs only while shown; Hide removes every animation clock and child, so a hidden layer costs nothing.
internal sealed class SeasonalAmbientLayer : Canvas
{
	private sealed class Layer
	{
		public Rectangle Rect = null!;

		public TranslateTransform Move = null!;

		public TranslateTransform Sway = null!;

		public Size Tile;

		public int Index;
	}

	private readonly List<Layer> _layers = new List<Layer>();

	private readonly List<(Animatable Target, DependencyProperty Property, AnimationTimeline Animation)> _running = new List<(Animatable, DependencyProperty, AnimationTimeline)>();

	// QA only: the transforms animated before the last Hide, to prove their clocks were released.
	private readonly List<Animatable> _retired = new List<Animatable>();

	private static readonly Dictionary<string, (DrawingBrush Brush, Size Tile)> BrushCache = new Dictionary<string, (DrawingBrush, Size)>(StringComparer.Ordinal);

	private string _kind = "";

	private Color _accent;

	private MotionMode _mode;

	private bool _shown;

	// Height of the band at the top over which the effect fades in (DIU).
	private const double HeaderFade = 230.0;

	public SeasonalAmbientLayer()
	{
		IsHitTestVisible = false;
		Focusable = false;
		ClipToBounds = true;
		// Fade the effect out toward the top, so flakes and bats do not flicker through the Start title, the user
		// name and the group names.
		LinearGradientBrush mask = new LinearGradientBrush
		{
			MappingMode = BrushMappingMode.Absolute,
			StartPoint = new Point(0.0, 0.0),
			EndPoint = new Point(0.0, HeaderFade)
		};
		mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0));
		mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.5));
		mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 1.0));
		mask.Freeze();
		OpacityMask = mask;
		SizeChanged += delegate
		{
			LayoutLayers();
		};
	}

	public bool IsShown => _shown;

	// Idempotent for the same kind, accent and motion mode, so repeated background refreshes do not restart it.
	public void Show(string kind, Color accent, MotionMode mode)
	{
		if (_shown && kind == _kind && accent == _accent && mode == _mode)
		{
			return;
		}
		Hide();
		_kind = kind;
		_accent = accent;
		_mode = mode;
		_shown = true;
		for (int i = 0; i < 2; i++)
		{
			(DrawingBrush brush, Size tile) = TileBrush(kind, i, accent);
			Layer layer = new Layer
			{
				Move = new TranslateTransform(),
				Sway = new TranslateTransform(),
				Tile = tile,
				Index = i
			};
			TransformGroup group = new TransformGroup();
			group.Children.Add(layer.Move);
			group.Children.Add(layer.Sway);
			layer.Rect = new Rectangle
			{
				Fill = brush,
				IsHitTestVisible = false,
				RenderTransform = group,
				Opacity = LayerOpacity(kind, i),
				Visibility = (i == 1 && mode == MotionMode.Reduced) ? Visibility.Collapsed : Visibility.Visible
			};
			_layers.Add(layer);
			Children.Add(layer.Rect);
		}
		LayoutLayers();
		if (mode != MotionMode.Off)
		{
			StartAnimations();
		}
	}

	public void Hide()
	{
		_retired.Clear();
		foreach ((Animatable target, DependencyProperty property, _) in _running)
		{
			target.BeginAnimation(property, null);
			_retired.Add(target);
		}
		_running.Clear();
		_layers.Clear();
		Children.Clear();
		_shown = false;
	}

	internal (int Clocks, int[] DesiredFps, bool NearVisible) QaState()
	{
		int clocks = _running.Count(r => r.Target.HasAnimatedProperties);
		int[] fps = _running.Select(r => Timeline.GetDesiredFrameRate(r.Animation) ?? 0).ToArray();
		bool near = _layers.Any(l => l.Index == 1 && l.Rect.Visibility == Visibility.Visible);
		return (clocks, fps, near);
	}

	internal bool QaRetiredStillAnimated()
	{
		return _retired.Any(t => t.HasAnimatedProperties);
	}

	internal int QaSwayClocks()
	{
		return _running.Count(r => _layers.Any(l => ReferenceEquals(l.Sway, r.Target)));
	}

	private static double LayerOpacity(string kind, int layer)
	{
		if (kind == "bats")
		{
			return layer == 0 ? 0.75 : 0.95;
		}
		return layer == 0 ? 0.7 : 0.8;
	}

	private static (DrawingBrush Brush, Size Tile) TileBrush(string kind, int layer, Color accent)
	{
		string key = $"{kind}|{layer}|{accent}";
		lock (BrushCache)
		{
			if (!BrushCache.TryGetValue(key, out (DrawingBrush, Size) hit))
			{
				DrawingBrush brush = SeasonalArt.AmbientTile(kind, layer, accent, out Size tile);
				hit = (brush, tile);
				BrushCache[key] = hit;
			}
			return hit;
		}
	}

	private void LayoutLayers()
	{
		double w = Math.Max(1.0, ActualWidth);
		double h = Math.Max(1.0, ActualHeight);
		foreach (Layer layer in _layers)
		{
			SetLeft(layer.Rect, -layer.Tile.Width);
			SetTop(layer.Rect, -layer.Tile.Height);
			layer.Rect.Width = w + 2.0 * layer.Tile.Width;
			layer.Rect.Height = h + 2.0 * layer.Tile.Height;
		}
	}

	private void StartAnimations()
	{
		bool reduced = _mode == MotionMode.Reduced;
		double k = reduced ? 2.0 : 1.0;
		int fps = reduced ? 20 : 30;
		foreach (Layer layer in _layers)
		{
			if (layer.Rect.Visibility != Visibility.Visible)
			{
				continue;
			}
			bool near = layer.Index == 1;
			if (_kind == "bats")
			{
				Loop(layer.Move, TranslateTransform.XProperty, 0.0, -layer.Tile.Width, (near ? 28.0 : 40.0) * k, fps);
				if (!reduced)
				{
					Wave(layer.Sway, TranslateTransform.YProperty, near ? 14.0 : 8.0, near ? 2.6 : 3.4, fps);
				}
			}
			else
			{
				Loop(layer.Move, TranslateTransform.YProperty, 0.0, layer.Tile.Height, (near ? 8.5 : 11.0) * k, fps);
				if (!reduced)
				{
					Wave(layer.Sway, TranslateTransform.XProperty, near ? 22.0 : 12.0, near ? 5.6 : 4.2, fps);
				}
			}
		}
	}

	private void Loop(TranslateTransform target, DependencyProperty property, double from, double to, double seconds, int fps)
	{
		DoubleAnimation anim = new DoubleAnimation(from, to, new Duration(TimeSpan.FromSeconds(seconds)))
		{
			RepeatBehavior = RepeatBehavior.Forever
		};
		Begin(target, property, anim, fps);
	}

	private void Wave(TranslateTransform target, DependencyProperty property, double amplitude, double seconds, int fps)
	{
		DoubleAnimation anim = new DoubleAnimation(-amplitude, amplitude, new Duration(TimeSpan.FromSeconds(seconds)))
		{
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever,
			EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
		};
		Begin(target, property, anim, fps);
	}

	private void Begin(TranslateTransform target, DependencyProperty property, DoubleAnimation anim, int fps)
	{
		Timeline.SetDesiredFrameRate(anim, fps);
		anim.Freeze();
		target.BeginAnimation(property, anim);
		_running.Add((target, property, anim));
	}
}
