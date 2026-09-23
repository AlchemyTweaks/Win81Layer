#nullable enable

using System.Windows;
using System.Windows.Controls;

namespace Win81Layer;

internal static class DesktopContextMenu
{
	private static ContextMenu? _cachedMenu;

	private static MenuItem? _large;

	private static MenuItem? _medium;

	private static MenuItem? _small;

	private static MenuItem? _name;

	private static MenuItem? _size;

	private static MenuItem? _type;

	private static MenuItem? _modified;

	private static MenuItem? _paste;

	static DesktopContextMenu()
	{
		ShellNewItems.CacheReady += delegate
		{
			System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
			if (dispatcher == null || dispatcher.HasShutdownStarted) return;
			dispatcher.BeginInvoke((System.Action)delegate
			{
				_cachedMenu = null;
				Warm();
			}, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
		};
	}

	internal static void Warm()
	{
		ContextMenu menu = Build();
		TaskbarContextMenu.PrepareForInstantOpen(menu);
	}

	internal static ContextMenu Build()
	{
		if (_cachedMenu != null)
		{
			RefreshState(_cachedMenu);
			return _cachedMenu;
		}
		ResourceDictionary res = Application.Current.Resources;
		ContextMenu ctx = new ContextMenu
		{
			Style = (Style)res["Win81ContextMenu"]
		};
		TaskbarContextMenu.ApplyTheme(ctx);
		_large = TaskbarContextMenu.Choice("Large icons", false, delegate
		{
			DesktopView.SetIconSize(96);
		});
		_medium = TaskbarContextMenu.Choice("Medium icons", false, delegate
		{
			DesktopView.SetIconSize(48);
		});
		_small = TaskbarContextMenu.Choice("Small icons", false, delegate
		{
			DesktopView.SetIconSize(32);
		});
		ctx.Items.Add(TaskbarContextMenu.Sub81("View", "win2012.png", 57656, _large, _medium, _small));
		_name = TaskbarContextMenu.Choice("Name", false, delegate
		{
			DesktopView.SortBy(DesktopView.PkName, ascending: true);
		});
		_size = TaskbarContextMenu.Choice("Size", false, delegate
		{
			DesktopView.SortBy(DesktopView.PkSize, ascending: true);
		});
		_type = TaskbarContextMenu.Choice("Item type", false, delegate
		{
			DesktopView.SortBy(DesktopView.PkType, ascending: true);
		});
		_modified = TaskbarContextMenu.Choice("Date modified", false, delegate
		{
			DesktopView.SortBy(DesktopView.PkDateModified, ascending: false);
		});
		ctx.Items.Add(TaskbarContextMenu.Sub("Sort by", 59595, _name, _size, _type, _modified));
		ctx.Items.Add(TaskbarContextMenu.Leaf("Refresh", 57673, DesktopShell.Refresh));
		ctx.Items.Add(TaskbarContextMenu.Sep());
		_paste = TaskbarContextMenu.Leaf("Paste", DesktopShell.Paste, enabled: false);
		_paste.Icon = TaskbarContextMenu.MenuGlyph(59263);
		ctx.Items.Add(_paste);
		ctx.Items.Add(TaskbarContextMenu.Sep());
		ctx.Items.Add(TaskbarContextMenu.Leaf81("Open command prompt here", "cmd.png", 59222, DesktopShell.OpenTerminalHere));
		ctx.Items.Add(TaskbarContextMenu.Leaf81("Open PowerShell here", "powershell.png", 59222, DesktopShell.OpenPowerShellHere));
		MenuItem psAdmin = TaskbarContextMenu.Leaf("Open PowerShell here (Admin)", DesktopShell.OpenPowerShellHereAdmin);
		psAdmin.Icon = TaskbarContextMenu.MenuStockIcon(TaskbarContextMenu.SIID_SHIELD, 57767);   // authentic UAC shield
		ctx.Items.Add(psAdmin);
		ctx.Items.Add(TaskbarContextMenu.Sep());
		ctx.Items.Add(TaskbarContextMenu.PerformanceToolsSub());
		ctx.Items.Add(TaskbarContextMenu.SystemToolsSub());
		ctx.Items.Add(TaskbarContextMenu.Sep());
		System.Collections.Generic.List<object> newChildren = new System.Collections.Generic.List<object>
		{
			TaskbarContextMenu.LeafFolderIcon("Folder", 57736, DesktopShell.NewFolder),
			TaskbarContextMenu.LeafExtIcon("Shortcut", ".lnk", 59391, DesktopShell.NewShortcut),
			// Text Document is ALWAYS present (not gated on the ShellNew cache being warm), so "create a txt" never
			// vanishes when the menu is built before the async warm finishes.
			TaskbarContextMenu.LeafFileIcon("Text Document", ".txt", 59331, delegate
			{
				DesktopShell.NewFile(new ShellNewItem { Label = "Text Document", Ext = ".txt", Kind = ShellNewKind.NullFile });
			})
		};
		// Enumerate() (not EnumerateFast) so the first, cached menu build gets the full authentic New list even before
		// the background warm completes. .txt is added explicitly above, so skip its duplicate here.
		System.Collections.Generic.List<ShellNewItem> shellNew = ShellNewItems.Enumerate();
		System.Collections.Generic.List<object> extraNew = new System.Collections.Generic.List<object>();
		foreach (ShellNewItem sni in shellNew)
		{
			if (sni.Ext.Equals(".txt", System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			ShellNewItem item = sni;   // capture per iteration
			extraNew.Add(TaskbarContextMenu.LeafFileIcon(item.Label, item.Ext, 59331, delegate
			{
				DesktopShell.NewFile(item);
			}));
		}
		if (extraNew.Count > 0)
		{
			newChildren.Add(TaskbarContextMenu.Sep());
			newChildren.AddRange(extraNew);
		}
		ctx.Items.Add(TaskbarContextMenu.Sub("New", 57609, newChildren.ToArray()));
		ctx.Items.Add(TaskbarContextMenu.Sep());
		ctx.Items.Add(TaskbarContextMenu.Leaf("Screen resolution", 59380, delegate
		{
			TaskbarContextMenu.OpenUri("ms-settings:display");
		}));
		ctx.Items.Add(TaskbarContextMenu.Leaf("Personalize", 59249, delegate
		{
			TaskbarContextMenu.OpenUri("ms-settings:personalization");
		}));
		_cachedMenu = ctx;
		RefreshState(ctx);
		return ctx;
	}

	private static void RefreshState(ContextMenu menu)
	{
		TaskbarContextMenu.ApplyTheme(menu);
		var (iconSize, sortPid, ok) = DesktopView.GetState();
		if (_large != null) _large.IsChecked = ok && iconSize >= 72;
		if (_medium != null) _medium.IsChecked = ok && iconSize >= 40 && iconSize < 72;
		if (_small != null) _small.IsChecked = ok && iconSize > 0 && iconSize < 40;
		if (_name != null) _name.IsChecked = ok && sortPid == 10;
		if (_size != null) _size.IsChecked = ok && sortPid == 12;
		if (_type != null) _type.IsChecked = ok && sortPid == 4;
		if (_modified != null) _modified.IsChecked = ok && sortPid == 14;
		if (_paste != null) _paste.IsEnabled = DesktopShell.CanPaste();
	}
}
