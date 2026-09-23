using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

internal static class DesktopView
{
	private static readonly object StateGate = new object();

	private static (int iconSize, uint sortPid, bool ok) _cachedState;

	private static long _cachedStateAtMs;

	private static int _stateRefreshPending;

	[ComImport]
	[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellWindows
	{
		void _GetTypeInfoCount();

		void _GetTypeInfo();

		void _GetIDsOfNames();

		void _Invoke();

		void _get_Count();

		void _Item();

		void _NewEnum();

		void _Register();

		void _RegisterPending();

		void _Revoke();

		void _OnNavigate();

		void _OnActivated();

		[PreserveSig]
		int FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int pHWND, int swfwOptions, [MarshalAs(UnmanagedType.IDispatch)] out object ppdispOut);
	}

	[ComImport]
	[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IServiceProvider
	{
		[PreserveSig]
		int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
	}

	[ComImport]
	[Guid("000214E2-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellBrowser
	{
		void _GetWindow();

		void _ContextSensitiveHelp();

		void _InsertMenusSB();

		void _SetMenuSB();

		void _RemoveMenusSB();

		void _SetStatusTextSB();

		void _EnableModelessSB();

		void _TranslateAcceleratorSB();

		void _BrowseObject();

		void _GetViewStateStream();

		void _GetControlWindow();

		void _SendControlMsg();

		[PreserveSig]
		int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object ppshv);
	}

	[ComImport]
	[Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IFolderView2
	{
		void _GetCurrentViewMode();

		void _SetCurrentViewMode();

		void _GetFolder();

		void _Item();

		void _ItemCount();

		void _Items();

		void _GetSelectionMarkedItem();

		void _GetFocusedItem();

		void _GetItemPosition();

		void _GetSpacing();

		void _GetDefaultSpacing();

		void _GetAutoArrange();

		void _SelectItem();

		void _SelectAndPositionItems();

		void _SetGroupBy();

		void _GetGroupBy();

		void _SetViewProperty();

		void _GetViewProperty();

		void _SetTileViewProperties();

		void _SetExtendedTileViewProperties();

		void _SetText();

		void _SetCurrentFolderFlags();

		void _GetCurrentFolderFlags();

		void _GetSortColumnCount();

		[PreserveSig]
		int SetSortColumns([MarshalAs(UnmanagedType.LPArray)] SORTCOLUMN[] rgSortColumns, int cColumns);

		[PreserveSig]
		int GetSortColumns([Out][MarshalAs(UnmanagedType.LPArray)] SORTCOLUMN[] rgSortColumns, int cColumns);

		void _GetItem();

		void _GetVisibleItem();

		void _GetSelectedItem();

		void _GetSelection();

		void _GetSelectionState();

		void _InvokeVerbOnSelection();

		[PreserveSig]
		int SetViewModeAndIconSize(uint uViewMode, int iImageSize);

		[PreserveSig]
		int GetViewModeAndIconSize(out uint puViewMode, out int piImageSize);
	}

	internal const int IconLarge = 96;

	internal const int IconMedium = 48;

	internal const int IconSmall = 32;

	private const uint FVM_ICON = 1u;

	private static readonly Guid Storage = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC");

	private static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

	private static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");

	private static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");

	private const int SWC_DESKTOP = 8;

	private const int SWFO_NEEDDISPATCH = 1;

	private const int CSIDL_DESKTOP = 0;

	internal static PROPERTYKEY PkName => new PROPERTYKEY
	{
		fmtid = Storage,
		pid = 10u
	};

	internal static PROPERTYKEY PkSize => new PROPERTYKEY
	{
		fmtid = Storage,
		pid = 12u
	};

	internal static PROPERTYKEY PkType => new PROPERTYKEY
	{
		fmtid = Storage,
		pid = 4u
	};

	internal static PROPERTYKEY PkDateModified => new PROPERTYKEY
	{
		fmtid = Storage,
		pid = 14u
	};

	internal static void SetIconSize(int px)
	{
		lock (StateGate)
		{
			_cachedState = (px, _cachedState.sortPid, true);
			_cachedStateAtMs = Environment.TickCount64;
		}
		RunSta("Desktop SetIconSize", delegate
		{
			GetDesktopView()?.SetViewModeAndIconSize(1u, px);
		});
	}

	internal static (int iconSize, uint sortPid, bool ok) GetState()
	{
		(int iconSize, uint sortPid, bool ok) state;
		lock (StateGate)
		{
			state = _cachedState;
		}
		RefreshStateAsync();
		return state;
	}

	internal static void Warm()
	{
		RefreshStateAsync();
	}

	private static void RefreshStateAsync()
	{
		lock (StateGate)
		{
			if (_cachedState.ok && Environment.TickCount64 - _cachedStateAtMs < 750L)
			{
				return;
			}
		}
		if (Interlocked.Exchange(ref _stateRefreshPending, 1) != 0)
		{
			return;
		}
		Thread thread = new Thread(delegate()
		{
			try
			{
				var state = QueryState();
				lock (StateGate)
				{
					_cachedState = state;
					_cachedStateAtMs = Environment.TickCount64;
				}
			}
			finally
			{
				Interlocked.Exchange(ref _stateRefreshPending, 0);
			}
		})
		{
			IsBackground = true,
			Name = "DesktopViewState",
			Priority = ThreadPriority.BelowNormal
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	private static (int iconSize, uint sortPid, bool ok) QueryState()
	{
		try
		{
			IFolderView2 fv = GetDesktopView();
			if (fv == null)
			{
				return (iconSize: 0, sortPid: 0u, ok: false);
			}
			fv.GetViewModeAndIconSize(out var _, out var size);
			uint pid = 0u;
			SORTCOLUMN[] cols = new SORTCOLUMN[1];
			if (fv.GetSortColumns(cols, 1) == 0)
			{
				pid = cols[0].propkey.pid;
			}
			return (iconSize: size, sortPid: pid, ok: true);
		}
		catch (Exception ex)
		{
			Logger.Log("Desktop GetState failed: " + ex.Message);
			return (iconSize: 0, sortPid: 0u, ok: false);
		}
	}

	internal static void SortBy(PROPERTYKEY key, bool ascending)
	{
		lock (StateGate)
		{
			_cachedState = (_cachedState.iconSize, key.pid, true);
			_cachedStateAtMs = Environment.TickCount64;
		}
		RunSta("Desktop SortBy", delegate
		{
			IFolderView2 fv = GetDesktopView();
			if (fv == null)
			{
				return;
			}
			SORTCOLUMN col = new SORTCOLUMN
			{
				propkey = key,
				direction = (ascending ? 1 : (-1))
			};
			fv.SetSortColumns(new SORTCOLUMN[1] { col }, 1);
		});
	}

	private static void RunSta(string operation, Action action)
	{
		Thread thread = new Thread(delegate()
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				Logger.Log(operation + " failed: " + ex.Message);
			}
		})
		{
			IsBackground = true,
			Name = operation.Replace(" ", ""),
			Priority = ThreadPriority.AboveNormal
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	private static IFolderView2? GetDesktopView()
	{
		Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
		if ((object)t == null)
		{
			return null;
		}
		IShellWindows shellWindows = (IShellWindows)Activator.CreateInstance(t);
		if (shellWindows == null)
		{
			return null;
		}
		object loc = 0;
		object empty = 0;
		if (shellWindows.FindWindowSW(ref loc, ref empty, 8, out int _, 1, out object disp) != 0 || disp == null)
		{
			return null;
		}
		if (!(disp is IServiceProvider sp))
		{
			return null;
		}
		Guid sidBrowser = SID_STopLevelBrowser;
		Guid iidBrowser = IID_IShellBrowser;
		if (sp.QueryService(ref sidBrowser, ref iidBrowser, out object browserObj) != 0 || !(browserObj is IShellBrowser browser))
		{
			return null;
		}
		if (browser.QueryActiveShellView(out object shellView) != 0 || shellView == null)
		{
			return null;
		}
		return shellView as IFolderView2;
	}
}
