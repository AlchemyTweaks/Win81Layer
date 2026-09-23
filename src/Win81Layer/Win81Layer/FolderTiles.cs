using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

internal static class FolderTiles
{
	private static int _phaseSeed;

	internal static ImageSource? Composite(IReadOnlyList<AppEntry> members, int offset)
	{
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (members.Count == 0)
			{
				return null;
			}
			List<ImageSource> icons = new List<ImageSource>();
			int take = Math.Min(4, members.Count);
			for (int k = 0; k < take; k++)
			{
				ImageSource ic = members[(offset + k) % members.Count].Icon;
				if (ic != null)
				{
					icons.Add(ic);
				}
			}
			if (icons.Count == 0)
			{
				return null;
			}
			DrawingVisual dv = new DrawingVisual();
			using (DrawingContext dc = dv.RenderOpen())
			{
				for (int i = 0; i < icons.Count; i++)
				{
					double x = 6 + i % 2 * 44;
					double y = 6 + i / 2 * 44;
					dc.DrawImage(icons[i], new Rect(x, y, 40.0, 40.0));
				}
			}
			RenderTargetBitmap rtb = new RenderTargetBitmap(96, 96, 96.0, 96.0, PixelFormats.Pbgra32);
			rtb.Render(dv);
			((Freezable)rtb).Freeze();
			return rtb;
		}
		catch
		{
			return (members.Count > 0) ? members[0].Icon : null;
		}
	}

	internal static AppEntry MakeFolderEntry(string name)
	{
		return new AppEntry
		{
			Name = name,
			LaunchPath = "folder:",
			TileBrush = new SolidColorBrush(Color.FromArgb(230, 42, 42, 42))
		};
	}

	internal static TileVm MakeFolder(string name, IEnumerable<AppEntry> members, TileSize size = TileSize.Medium)
	{
		TileVm vm = new TileVm
		{
			Entry = MakeFolderEntry(name),
			Live = LiveKind.Folder,
			Size = size,
			CyclePhase = _phaseSeed++
		};
		foreach (AppEntry m in members)
		{
			vm.Members.Add(m);
		}
		vm.RebuildFolderIcon();
		return vm;
	}
}
