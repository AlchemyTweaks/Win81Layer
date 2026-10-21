using System;
using System.Collections.Generic;

namespace Win81Layer;

// One user gesture on a tray icon, as the notification area reports it to the icon's owner.
internal enum TrayEvent
{
	LeftClick,
	// The whole second half of a double click, press and release together (a held click delivered later; the first
	// press was already sent as a LeftClick).
	LeftDoubleClick,
	RightClick,
	MiddleClick,
	Hover,
	// Enter or Space on a focused icon.
	KeySelect,
	// Shift+F10 or the Menu key on a focused icon.
	KeyContextMenu,
	// The second press of a double click, sent when the button goes down (as Explorer sends WM_LBUTTONDBLCLK).
	LeftDoubleClickPress,
	// The release that ends it.
	LeftDoubleClickRelease,
	// A second right / middle click within the double-click time: the DBLCLK message takes the place of the DOWN.
	RightDoubleClick,
	MiddleDoubleClick
}

// One message for the icon's callback: the event and how it is packed into wParam / lParam.
internal readonly struct TrayMessage
{
	public readonly uint Event;

	public readonly nint WParam;

	public readonly nint LParam;

	public TrayMessage(uint evt, nint wParam, nint lParam)
	{
		Event = evt;
		WParam = wParam;
		LParam = lParam;
	}

	public override string ToString()
	{
		return $"{TrayProtocol.EventName(Event)} w=0x{(long)WParam:X} l=0x{(long)LParam:X}";
	}
}

// What Explorer sends to a notification icon's callback message, per the NOTIFYICON version the icon registered.
// Apps are written and tested against Explorer, so the tray must send exactly this and nothing else.
//
// Packing:
//   version 0 and 3: wParam = uID, lParam = the event.
//   version 4:       wParam = MAKEWPARAM(x, y), the anchor in physical screen pixels (the cursor for mouse events, the
//                    icon's upper-left corner for keyboard events); lParam = MAKELPARAM(event, uID). Both zero-extended.
// Sequences, sent in order when the gesture completes:
//   left click     WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP, then NIN_SELECT for version 3 and 4
//   double click   second press: WM_LBUTTONDBLCLK at once; its release: WM_LBUTTONUP, then NIN_SELECT for version 3 and 4
//                  (a timer an app starts on the first WM_LBUTTONUP is cancelled by the DBLCLK in time)
//   right click    WM_MOUSEMOVE, WM_RBUTTONDOWN, WM_RBUTTONUP, then WM_CONTEXTMENU for version 3 and 4 only. Version 0
//                  never gets WM_CONTEXTMENU: Electron and Chromium open their menu on WM_RBUTTONDOWN and again on
//                  WM_CONTEXTMENU, so the second one would close or break the first.
//   right double   WM_RBUTTONDBLCLK, WM_RBUTTONUP, then WM_CONTEXTMENU for version 3 and 4
//   middle click   WM_MOUSEMOVE, WM_MBUTTONDOWN, WM_MBUTTONUP
//   middle double  WM_MBUTTONDBLCLK, WM_MBUTTONUP
//   hover          WM_MOUSEMOVE
//   Enter / Space  NIN_KEYSELECT for version 3 and 4; WM_RBUTTONDOWN, WM_RBUTTONUP for version 0 (as documented)
//   Shift+F10      WM_CONTEXTMENU for version 3 and 4; WM_RBUTTONDOWN, WM_RBUTTONUP for version 0 (as documented)
// Version 4 apps also get the mouse messages (Hardcodet WPF icons act only on WM_LBUTTONUP / WM_RBUTTONUP).
// When the version is unknown (the legacy toolbar source reads no version), see EventsUnknownVersion.
internal static class TrayProtocol
{
	public const uint WM_CONTEXTMENU = 0x007B;

	public const uint WM_MOUSEMOVE = 0x0200;

	public const uint WM_LBUTTONDOWN = 0x0201;

	public const uint WM_LBUTTONUP = 0x0202;

	public const uint WM_LBUTTONDBLCLK = 0x0203;

	public const uint WM_RBUTTONDOWN = 0x0204;

	public const uint WM_RBUTTONUP = 0x0205;

	public const uint WM_RBUTTONDBLCLK = 0x0206;

	public const uint WM_MBUTTONDOWN = 0x0207;

	public const uint WM_MBUTTONUP = 0x0208;

	public const uint WM_MBUTTONDBLCLK = 0x0209;

	public const uint NIN_SELECT = 0x0400;

	public const uint NIN_KEYSELECT = 0x0401;

	public const uint NOTIFYICON_VERSION = 3;

	public const uint NOTIFYICON_VERSION_4 = 4;

	public static bool IsVersion4(uint version)
	{
		return version >= NOTIFYICON_VERSION_4;
	}

	public static bool HasSelectAndContextMenu(uint version)
	{
		return version >= NOTIFYICON_VERSION;
	}

	// The events Explorer delivers for one gesture, in order.
	public static List<uint> Events(uint version, TrayEvent ev)
	{
		bool modern = HasSelectAndContextMenu(version);
		List<uint> list = new List<uint>(4);
		switch (ev)
		{
		case TrayEvent.LeftClick:
			list.Add(WM_MOUSEMOVE);
			list.Add(WM_LBUTTONDOWN);
			list.Add(WM_LBUTTONUP);
			if (modern)
			{
				list.Add(NIN_SELECT);
			}
			break;
		case TrayEvent.LeftDoubleClick:
			list.Add(WM_LBUTTONDBLCLK);
			list.Add(WM_LBUTTONUP);
			if (modern)
			{
				list.Add(NIN_SELECT);
			}
			break;
		case TrayEvent.RightClick:
			list.Add(WM_MOUSEMOVE);
			list.Add(WM_RBUTTONDOWN);
			list.Add(WM_RBUTTONUP);
			if (modern)
			{
				list.Add(WM_CONTEXTMENU);
			}
			break;
		case TrayEvent.MiddleClick:
			list.Add(WM_MOUSEMOVE);
			list.Add(WM_MBUTTONDOWN);
			list.Add(WM_MBUTTONUP);
			break;
		case TrayEvent.Hover:
			list.Add(WM_MOUSEMOVE);
			break;
		case TrayEvent.KeySelect:
			if (modern)
			{
				list.Add(NIN_KEYSELECT);
			}
			else
			{
				list.Add(WM_RBUTTONDOWN);
				list.Add(WM_RBUTTONUP);
			}
			break;
		case TrayEvent.KeyContextMenu:
			if (modern)
			{
				list.Add(WM_CONTEXTMENU);
			}
			else
			{
				list.Add(WM_RBUTTONDOWN);
				list.Add(WM_RBUTTONUP);
			}
			break;
		case TrayEvent.LeftDoubleClickPress:
			list.Add(WM_LBUTTONDBLCLK);
			break;
		case TrayEvent.LeftDoubleClickRelease:
			list.Add(WM_LBUTTONUP);
			if (modern)
			{
				list.Add(NIN_SELECT);
			}
			break;
		case TrayEvent.RightDoubleClick:
			list.Add(WM_RBUTTONDBLCLK);
			list.Add(WM_RBUTTONUP);
			if (modern)
			{
				list.Add(WM_CONTEXTMENU);
			}
			break;
		case TrayEvent.MiddleDoubleClick:
			list.Add(WM_MBUTTONDBLCLK);
			list.Add(WM_MBUTTONUP);
			break;
		}
		return list;
	}

	// The icon's version is unknown: the legacy toolbar source (Win8.1 / Win10 Explorer's notification toolbar) carries
	// none. Version 0 sequence, then the version 3 notification of the same gesture (NIN_SELECT, NIN_KEYSELECT,
	// WM_CONTEXTMENU): a version 3 / 4 icon (Qt, SDK-style Win32) opens its menu or acts only on those, and a version 0
	// icon ignores them. Except for the Chromium family (Electron, Chrome's status tray), which is known to be version 0
	// and opens its menu on WM_CONTEXTMENU as well, so the extra message would pop the menu a second time.
	public static List<uint> EventsUnknownVersion(TrayEvent ev, bool chromiumFamily)
	{
		List<uint> list = Events(0u, ev);
		if (chromiumFamily)
		{
			return list;
		}
		foreach (uint evt in Events(NOTIFYICON_VERSION, ev))
		{
			if (evt == NIN_SELECT || evt == NIN_KEYSELECT || evt == WM_CONTEXTMENU)
			{
				list.Add(evt);
			}
		}
		return list;
	}

	// The messages for one gesture, packed for the icon's version. x, y: the anchor in physical screen pixels.
	public static List<TrayMessage> Build(uint version, uint id, TrayEvent ev, int x, int y)
	{
		List<uint> events = Events(version, ev);
		List<TrayMessage> list = new List<TrayMessage>(events.Count);
		foreach (uint evt in events)
		{
			list.Add(Pack(version, id, evt, x, y));
		}
		return list;
	}

	// The same for an icon of unknown version, packed as version 0 (wParam = uID, lParam = the event).
	public static List<TrayMessage> BuildUnknownVersion(uint id, TrayEvent ev, bool chromiumFamily)
	{
		List<uint> events = EventsUnknownVersion(ev, chromiumFamily);
		List<TrayMessage> list = new List<TrayMessage>(events.Count);
		foreach (uint evt in events)
		{
			list.Add(Pack(0u, id, evt, 0, 0));
		}
		return list;
	}

	public static TrayMessage Pack(uint version, uint id, uint evt, int x, int y)
	{
		if (IsVersion4(version))
		{
			return new TrayMessage(evt, MakeParam(x, y), MakeParam((int)evt, (int)id));
		}
		return new TrayMessage(evt, (nint)id, (nint)evt);
	}

	// MAKEWPARAM / MAKELPARAM: two 16-bit halves in a DWORD, zero-extended to pointer size (as Explorer's are).
	public static nint MakeParam(int lo, int hi)
	{
		return (nint)(((uint)hi << 16) | ((uint)lo & 0xFFFFu));
	}

	// A WPF ClickCount for a left press: Windows reports DOWN, DBLCLK, DOWN, DBLCLK, ... for rapid presses.
	public static bool IsDoubleClickPress(int clickCount)
	{
		return clickCount >= 2 && clickCount % 2 == 0;
	}

	public static string EventName(uint evt)
	{
		return evt switch
		{
			WM_CONTEXTMENU => "WM_CONTEXTMENU",
			WM_MOUSEMOVE => "WM_MOUSEMOVE",
			WM_LBUTTONDOWN => "WM_LBUTTONDOWN",
			WM_LBUTTONUP => "WM_LBUTTONUP",
			WM_LBUTTONDBLCLK => "WM_LBUTTONDBLCLK",
			WM_RBUTTONDOWN => "WM_RBUTTONDOWN",
			WM_RBUTTONUP => "WM_RBUTTONUP",
			WM_RBUTTONDBLCLK => "WM_RBUTTONDBLCLK",
			WM_MBUTTONDOWN => "WM_MBUTTONDOWN",
			WM_MBUTTONUP => "WM_MBUTTONUP",
			WM_MBUTTONDBLCLK => "WM_MBUTTONDBLCLK",
			NIN_SELECT => "NIN_SELECT",
			NIN_KEYSELECT => "NIN_KEYSELECT",
			_ => "0x" + evt.ToString("X")
		};
	}
}
