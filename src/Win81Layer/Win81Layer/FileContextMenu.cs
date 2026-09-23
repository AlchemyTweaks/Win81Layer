#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Win81Layer;

internal static class FileContextMenu
{
	private sealed class MenuTarget
	{
		internal MenuTarget(string path, Action? openOverride)
		{
			Path = path;
			OpenOverride = openOverride;
		}

		internal string Path { get; }

		internal Action? OpenOverride { get; }
	}

	private static readonly Dictionary<int, ContextMenu> CachedMenus = new Dictionary<int, ContextMenu>();

	internal static Action<string>? PinToStart;

	internal static Action<string>? PinToTaskbar;

	internal static void Warm()
	{
		for (int kind = 0; kind < 3; kind++)
		{
			WarmKind(kind);
		}
	}

	internal static void WarmKind(int kind)
	{
		string system = Environment.SystemDirectory;
		ContextMenu menu = kind switch
		{
			0 => Build(system, isDir: true),
			1 => Build(Path.Combine(system, "notepad.exe"), isDir: false),
			_ => Build(Path.Combine(system, "desktop.ini"), isDir: false)
		};
		TaskbarContextMenu.PrepareForInstantOpen(menu);
	}

	internal static void AppendExtras(ContextMenu menu, string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		menu.Items.Add(TaskbarContextMenu.Leaf("Open with…", 59308, delegate
		{
			FileShell.OpenWith(path);
		}));
		if (PinToStart != null)
		{
			menu.Items.Add(TaskbarContextMenu.Leaf("Pin to Start", 57665, delegate
			{
				PinToStart(path);
			}));
		}
		menu.Items.Add(TaskbarContextMenu.Leaf("Properties", 59718, delegate
		{
			FileShell.Properties(path);
		}));
	}

	internal static ContextMenu Build(string path, bool isDir, Action? openOverride = null)
	{
		bool isExe = !isDir && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
		int key = (isDir ? 1 : 0)
			| (isExe ? 2 : 0)
			| (PinToStart != null ? 4 : 0)
			| (PinToTaskbar != null ? 8 : 0);
		if (!CachedMenus.TryGetValue(key, out ContextMenu? menu) || menu == null || menu.IsOpen)
		{
			menu = CreateMenu(isDir, isExe, PinToStart != null, PinToTaskbar != null);
			if (!menu.IsOpen)
			{
				CachedMenus[key] = menu;
			}
		}
		menu.Tag = new MenuTarget(path, openOverride);
		TaskbarContextMenu.ApplyTheme(menu);
		return menu;
	}

	// Win8.1 "Send to" submenu: the two common defaults done natively (Desktop shortcut, Compressed folder) plus any real
	// .lnk entries in the user's shell:sendto folder. The SendTo list is read at build time (it changes rarely).
	private static MenuItem BuildSendToSub(Func<MenuTarget> capture)
	{
		MenuItem sub = TaskbarContextMenu.Sub("Send to", 59172);
		sub.Items.Add(TaskbarContextMenu.LeafCaptured("Desktop (create shortcut)", 59163, capture, delegate(MenuTarget target)
		{
			FileShell.SendToDesktopShortcut(target.Path);
		}));
		sub.Items.Add(TaskbarContextMenu.LeafCaptured("Compressed (zipped) folder", 59575, capture, delegate(MenuTarget target)
		{
			FileShell.SendToZip(target.Path);
		}));
		System.Collections.Generic.List<(string Name, string LnkPath)> lnks = FileShell.SendToLnks();
		if (lnks.Count > 0)
		{
			sub.Items.Add(TaskbarContextMenu.Sep());
			foreach ((string Name, string LnkPath) entry in lnks)
			{
				string p = entry.LnkPath;
				sub.Items.Add(TaskbarContextMenu.LeafCaptured(entry.Name, 59391, capture, delegate(MenuTarget target)
				{
					FileShell.SendToLnk(p, target.Path);
				}));
			}
		}
		return sub;
	}

	private static ContextMenu CreateMenu(bool isDir, bool isExe, bool hasPinToStart, bool hasPinToTaskbar)
	{
		ResourceDictionary res = Application.Current.Resources;
		ContextMenu menu = new ContextMenu
		{
			Style = (Style)res["Win81ContextMenu"]
		};
		Func<MenuTarget> capture = delegate
		{
			return menu.Tag as MenuTarget ?? throw new InvalidOperationException("File context menu target is missing.");
		};
		menu.Items.Add(TaskbarContextMenu.LeafCaptured("Open", 59621, capture, delegate(MenuTarget target)
		{
			if (target.OpenOverride != null)
			{
				target.OpenOverride();
			}
			else
			{
				FileShell.Open(target.Path);
			}
		}));
		if (!isDir)
		{
			menu.Items.Add(TaskbarContextMenu.LeafCaptured("Open with…", 59308, capture, delegate(MenuTarget target)
			{
				FileShell.OpenWith(target.Path);
			}));
		}
		if (isDir)
		{
			menu.Items.Add(TaskbarContextMenu.Leaf81Captured("Open command prompt here", "cmd.png", 59222, capture, delegate(MenuTarget target)
			{
				FileShell.TerminalHere(target.Path, powershell: false);
			}));
			menu.Items.Add(TaskbarContextMenu.Leaf81Captured("Open PowerShell here", "powershell.png", 59222, capture, delegate(MenuTarget target)
			{
				FileShell.TerminalHere(target.Path, powershell: true);
			}));
		}
		if (isExe)
		{
			MenuItem runAsAdmin = TaskbarContextMenu.LeafCaptured("Run as administrator", 57767, capture, delegate(MenuTarget target)
			{
				FileShell.RunAsAdmin(target.Path);
			});
			runAsAdmin.Icon = TaskbarContextMenu.MenuStockIcon(TaskbarContextMenu.SIID_SHIELD, 57767);   // authentic UAC shield
			menu.Items.Add(runAsAdmin);
		}
		menu.Items.Add(TaskbarContextMenu.Sep());
		menu.Items.Add(TaskbarContextMenu.LeafCaptured("Cut", 59590, capture, delegate(MenuTarget target)
		{
			FileShell.Cut(target.Path);
		}));
		menu.Items.Add(TaskbarContextMenu.LeafCaptured("Copy", 59592, capture, delegate(MenuTarget target)
		{
			FileShell.Copy(target.Path);
		}));
		menu.Items.Add(TaskbarContextMenu.LeafCaptured("Copy as path", 59592, capture, delegate(MenuTarget target)
		{
			FileShell.CopyPath(target.Path, quoted: true);
		}));
		if (isDir)
		{
			// Paste INTO the folder (authentic Explorer offers this on a folder). Enablement is refreshed per-open since
			// the menu instance is cached; the handler is a no-op when the clipboard has no files.
			MenuItem pasteItem = TaskbarContextMenu.LeafCaptured("Paste", 59263, capture, delegate(MenuTarget target)
			{
				FileShell.PasteInto(target.Path);
			});
			menu.Items.Add(pasteItem);
			menu.Opened += delegate
			{
				try { pasteItem.IsEnabled = DesktopShell.CanPaste(); }
				catch { pasteItem.IsEnabled = true; }
			};
		}
		menu.Items.Add(TaskbarContextMenu.Sep());
		menu.Items.Add(TaskbarContextMenu.LeafCaptured("Open file location", 57736, capture, delegate(MenuTarget target)
		{
			FileShell.OpenLocation(target.Path);
		}));
		menu.Items.Add(TaskbarContextMenu.LeafCaptured("Create shortcut", 59163, capture, delegate(MenuTarget target)
		{
			FileShell.CreateShortcut(target.Path);
		}));
		menu.Items.Add(BuildSendToSub(capture));
		if ((isDir || isExe) && hasPinToStart)
		{
			menu.Items.Add(TaskbarContextMenu.LeafCaptured("Pin to Start", 57665, capture, delegate(MenuTarget target)
			{
				PinToStart?.Invoke(target.Path);
			}));
		}
		if (isExe && hasPinToTaskbar)
		{
			menu.Items.Add(TaskbarContextMenu.LeafCaptured("Pin to taskbar", 57665, capture, delegate(MenuTarget target)
			{
				PinToTaskbar?.Invoke(target.Path);
			}));
		}
		menu.Items.Add(TaskbarContextMenu.Sep());
		MenuItem renameItem = TaskbarContextMenu.LeafCaptured("Rename", 59564, capture, delegate(MenuTarget target)
		{
			string? text = TextPrompt.Show("Rename", Path.GetFileName(target.Path));
			if (!string.IsNullOrWhiteSpace(text) && text != Path.GetFileName(target.Path))
			{
				FileShell.Rename(target.Path, text);
			}
		});
		renameItem.Icon = TaskbarContextMenu.MenuStockIcon(TaskbarContextMenu.SIID_RENAME, 59564);
		menu.Items.Add(renameItem);
		MenuItem deleteItem = TaskbarContextMenu.LeafCaptured("Delete", 59213, capture, delegate(MenuTarget target)
		{
			FileShell.Recycle(target.Path);
		});
		deleteItem.Icon = TaskbarContextMenu.MenuStockIcon(TaskbarContextMenu.SIID_DELETE, 59213);
		menu.Items.Add(deleteItem);
		menu.Items.Add(TaskbarContextMenu.Sep());
		menu.Items.Add(TaskbarContextMenu.LeafCaptured("Properties", 59718, capture, delegate(MenuTarget target)
		{
			FileShell.Properties(target.Path);
		}));
		return menu;
	}
}
