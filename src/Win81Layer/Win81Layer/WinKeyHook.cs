using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

public sealed class WinKeyHook : IDisposable
{
	private delegate nint LowLevelProc(int nCode, nint wParam, nint lParam);

	private struct KBDLLHOOKSTRUCT
	{
		public int vkCode;

		public int scanCode;

		public int flags;

		public int time;

		public nuint dwExtraInfo;
	}

	private struct MSLLHOOKSTRUCT
	{
		public int ptX;

		public int ptY;

		public int mouseData;

		public int flags;

		public int time;

		public nuint dwExtraInfo;
	}

	private struct RECT
	{
		public int L;

		public int T;

		public int R;

		public int B;
	}

	private struct MSG
	{
		public nint hwnd;

		public uint message;

		public nint wParam;

		public nint lParam;

		public uint time;

		public int ptX;

		public int ptY;
	}

	private struct INPUT
	{
		public uint type;

		public InputUnion u;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)]
		public KEYBDINPUT ki;

		[FieldOffset(0)]
		public MOUSEINPUT mi;
	}

	private struct KEYBDINPUT
	{
		public ushort wVk;

		public ushort wScan;

		public uint dwFlags;

		public uint time;

		public nuint dwExtraInfo;
	}

	private struct MOUSEINPUT
	{
		public int dx;

		public int dy;

		public uint mouseData;

		public uint dwFlags;

		public uint time;

		public nuint dwExtraInfo;
	}

	private const int WH_KEYBOARD_LL = 13;

	private const int WH_MOUSE_LL = 14;

	private const int WM_KEYDOWN = 256;

	private const int WM_KEYUP = 257;

	private const int WM_SYSKEYDOWN = 260;

	private const int WM_SYSKEYUP = 261;

	private const int WM_LBUTTONDOWN = 513;

	private const int WM_RBUTTONDOWN = 516;

	private const int WM_RBUTTONUP = 517;

	private const int WM_MOUSEWHEEL = 522;

	private const int WM_QUIT = 18;

	private const uint MOUSEEVENTF_RIGHTDOWN = 8u;

	private const uint MOUSEEVENTF_RIGHTUP = 16u;

	private const int VK_LWIN = 91;

	private const int VK_RWIN = 92;

	private const int VK_CONTROL = 17;

	private const int VK_LCONTROL = 162;

	private const int VK_RCONTROL = 163;

	private const int VK_ESCAPE = 27;

	private const int VK_BACK = 8;

	private const int VK_RMENU = 165;   // right Alt = AltGr (LCtrl+RAlt); excluded from the panic combo so AltGr can't trigger it

	private const int VK_C = 67;

	private const int VK_I = 73;

	private const int VK_E = 69;

	private const int VK_TAB = 9;

	private const int VK_S = 83;

	private const int VK_SHIFT = 16;

	private const int VK_MENU = 18;

	private const int VK_LEFT = 37;

	private const int VK_RIGHT = 39;

	private const int VK_VOLUME_MUTE = 173;

	private const int VK_VOLUME_DOWN = 174;

	private const int VK_VOLUME_UP = 175;

	private static readonly nuint MagicExtra = 8519405u;

	private bool _swallowNextRUp;

	private int _rcX;

	private int _rcY;

	private bool _muteHeld;

	public static long LastTriggerTs;

	private readonly LowLevelProc _kbProc;

	private readonly LowLevelProc _mouseProc;

	private readonly Thread _thread;

	private uint _threadId;

	private nint _kbHook;

	private nint _mouseHook;

	private bool _winDown;

	private bool _otherKeyWhileWin;

	private bool _swallowCUp;

	private bool _swallowIUp;

	private bool _swallowEUp;

	private bool _swallowTabUp;

	private bool _swallowSUp;

	private bool _swallowAUp;

	private bool _swallowLeftUp;

	private bool _swallowRightUp;

	private bool _comboConsumed;

	private bool _ctrlDown;

	private readonly uint _ownPid = (uint)Environment.ProcessId;

	private readonly List<RECT> _startRects = new List<RECT>();

	private int _sbStamp = int.MinValue;

	public bool DominantMode { get; set; }

	public bool ReplaceDesktopMenu { get; set; } = true;

	private volatile bool _handleVolumeKeys;
	// Set from a background Task (audio COM init moved off the boot path); read on the hook thread — volatile so the
	// hook sees the flip promptly (worst case volume keys stay native for a few extra ms at boot).
	public bool HandleVolumeKeys { get { return _handleVolumeKeys; } set { _handleVolumeKeys = value; } }

	public bool ReplaceStartButton { get; set; } = true;

	// True while the Metro Start screen is visible. Gates the global Start-button click interception so that, when
	// Start is open, clicks in the bottom-left (e.g. the Metro Apps down-arrow) are never swallowed to toggle Start.
	public bool StartOpen { get; set; }

	public bool ReplaceExplorerShortcut { get; set; } = true;

	public event Action? WinTapped;

	public event Action? WinCTapped;

	public event Action? WinITapped;

	public event Action? WinETapped;

	public event Action? WinTabTapped;

	public event Action? WinSTapped;

	public event Action? WinATapped;

	// Panic / escape hatch: Ctrl+Alt+Shift+Backspace. Deliberately obscure so it can never fire by accident — it
	// cleanly exits the launcher and restores the native Windows shell (see App wiring -> ExitApp).
	public event Action? PanicRestore;

	public event Action? WinSnapLeft;

	public event Action? WinSnapRight;

	public event Action? WinMoveLeft;

	public event Action? WinMoveRight;

	public event Action? StartButtonClicked;

	public event Action<int, int>? GlobalLeftDown;

	public event Action<int, int>? GlobalRightDown;

	// Left-button up + mouse move, surfaced ONLY while TrackMouseDrag is set (the charms edge-pull gesture arms it on
	// an edge mouse-down and clears it on release), so there is zero per-move overhead when no gesture is in progress.
	public event Action<int, int>? GlobalLeftUp;

	public event Action<int, int>? GlobalMouseMove;

	public bool TrackMouseDrag;

	public event Func<int, int, int, bool>? GlobalMouseWheel;

	public event Action<int, int>? DesktopRightClick;

	public event Action? VolumeUp;

	public event Action? VolumeDown;

	public event Action? VolumeMute;

	public WinKeyHook()
	{
		_kbProc = KeyboardCallback;
		_mouseProc = MouseCallback;
		_thread = new Thread(HookThread)
		{
			IsBackground = true,
			Priority = ThreadPriority.Highest,
			Name = "InputHook"
		};
		_thread.Start();
	}

	private void HookThread()
	{
		_threadId = GetCurrentThreadId();
		_kbHook = SetWindowsHookEx(13, _kbProc, IntPtr.Zero, 0u);
		_mouseHook = SetWindowsHookEx(14, _mouseProc, IntPtr.Zero, 0u);
		if (_kbHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
		{
			Logger.Log($"SetWindowsHookEx failed: kb={_kbHook}, mouse={_mouseHook}, err={Marshal.GetLastWin32Error()}");
			return;
		}
		Logger.Log("Input hooks installed (keyboard + mouse)");
		MSG msg;
		while (GetMessage(out msg, IntPtr.Zero, 0u, 0u) > 0)
		{
			TranslateMessage(ref msg);
			DispatchMessage(ref msg);
		}
		if (_kbHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_kbHook);
		}
		if (_mouseHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_mouseHook);
		}
		_kbHook = (_mouseHook = IntPtr.Zero);
		Logger.Log("Input hooks removed");
	}

	private nint KeyboardCallback(int nCode, nint wParam, nint lParam)
	{
		if (nCode >= 0)
		{
			KBDLLHOOKSTRUCT info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
			if (info.dwExtraInfo != MagicExtra)
			{
				int msg = (int)wParam;
				bool flag = ((msg == 256 || msg == 260) ? true : false);
				bool isDown = flag;
				flag = ((msg == 257 || msg == 261) ? true : false);
				bool isUp = flag;
				int vkCode = info.vkCode;
				flag = (uint)(vkCode - 91) <= 1u;
				bool curIsWin = flag;
				vkCode = info.vkCode;
				flag = (uint)(vkCode - 162) <= 1u;
				bool curIsCtrl = flag;
				if (_winDown && !curIsWin && (GetAsyncKeyState(91) & 0x8000) == 0 && (GetAsyncKeyState(92) & 0x8000) == 0)
				{
					_winDown = false;
					_otherKeyWhileWin = false;
					_comboConsumed = false;
					_swallowCUp = false;
					_swallowIUp = false;
					_swallowEUp = false;
					_swallowTabUp = false;
					_swallowSUp = false;
					_swallowAUp = false;
					_swallowLeftUp = false;
					_swallowRightUp = false;
				}
				if (_ctrlDown && !curIsCtrl && (GetAsyncKeyState(17) & 0x8000) == 0)
				{
					_ctrlDown = false;
				}
				bool handleVolumeKeys = HandleVolumeKeys;
				bool flag2 = handleVolumeKeys;
				if (flag2)
				{
					vkCode = info.vkCode;
					flag = (uint)(vkCode - 173) <= 2u;
					flag2 = flag;
				}
				if (flag2)
				{
					if (isDown)
					{
						if (info.vkCode == 175)
						{
							Raise(VolumeUp);
						}
						else if (info.vkCode == 174)
						{
							Raise(VolumeDown);
						}
						else if (!_muteHeld)
						{
							_muteHeld = true;
							Raise(VolumeMute);
						}
					}
					else if (isUp && info.vkCode == 173)
					{
						_muteHeld = false;
					}
					return 1;
				}
				vkCode = info.vkCode;
				if ((uint)(vkCode - 162) <= 1u)
				{
					_ctrlDown = isDown;
				}
				// PANIC / escape hatch: Ctrl+Alt+Shift+Backspace -> cleanly exit the launcher and restore the native
				// Windows shell. Deliberately obscure so it can never fire by accident; swallowed so it never leaks to
				// the focused app. Handled before any other combo so it works even if another key-state machine is mid-flight.
				if (isDown && info.vkCode == VK_BACK
					&& (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
					&& (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
					&& (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0
					&& (GetAsyncKeyState(VK_RMENU) & 0x8000) == 0)   // exclude AltGr (RAlt): it injects LCtrl+RAlt and would else false-fire
				{
					Raise(PanicRestore);
					return 1;
				}
				vkCode = info.vkCode;
				if ((uint)(vkCode - 91) <= 1u)
				{
					if (isDown)
					{
						_winDown = true;
						_otherKeyWhileWin = false;
						_swallowCUp = (_swallowIUp = (_swallowEUp = (_swallowTabUp = (_swallowSUp = (_swallowLeftUp = (_swallowRightUp = false))))));
						_swallowAUp = false;
					}
					else if (isUp && _winDown)
					{
						_winDown = false;
						_swallowCUp = (_swallowIUp = (_swallowEUp = (_swallowTabUp = (_swallowSUp = (_swallowLeftUp = (_swallowRightUp = false))))));
						_swallowAUp = false;
						bool bareTap = !_otherKeyWhileWin;
						bool consumedCombo = _comboConsumed;
						_comboConsumed = false;
						if (bareTap | consumedCombo)
						{
							ushort vk = (ushort)info.vkCode;
							if (bareTap)
							{
								LastTriggerTs = Stopwatch.GetTimestamp();
							}
							ThreadPool.QueueUserWorkItem(delegate
							{
								InjectCancelSequence(vk);
								if (bareTap)
								{
									Raise(WinTapped);
								}
							});
							return 1;
						}
					}
				}
				else
				{
					if (((info.vkCode == 27) & isDown) && _ctrlDown && !_winDown)
					{
						LastTriggerTs = Stopwatch.GetTimestamp();
						Raise(WinTapped);
						return 1;
					}
					if (isDown && _winDown)
					{
						_otherKeyWhileWin = true;
						if (info.vkCode == 67)
						{
							_swallowCUp = true;
							_comboConsumed = true;
							Raise(WinCTapped);
							return 1;
						}
						if (info.vkCode == 73)
						{
							_swallowIUp = true;
							_comboConsumed = true;
							Raise(WinITapped);
							return 1;
						}
						if (info.vkCode == VK_E && DominantMode && ReplaceExplorerShortcut && !OtherModifierHeld())
						{
							if (!_swallowEUp)
							{
								_comboConsumed = true;
								Raise(WinETapped);
							}
							_swallowEUp = true;
							return 1;
						}
						if (info.vkCode == 9)
						{
							_swallowTabUp = true;
							_comboConsumed = true;
							Raise(WinTabTapped);
							return 1;
						}
						if (info.vkCode == 83 && DominantMode && !OtherModifierHeld())
						{
							if (!_swallowSUp)
							{
								_comboConsumed = true;
								Raise(WinSTapped);
							}
							_swallowSUp = true;
							return 1;
						}
						// Win+A → our Action Center (suppress the native one). Gated on DominantMode like Win+S.
						if (info.vkCode == 65 && DominantMode && !OtherModifierHeld())
						{
							if (!_swallowAUp)
							{
								_comboConsumed = true;
								Raise(WinATapped);
							}
							_swallowAUp = true;
							return 1;
						}
						if ((info.vkCode == 37 || info.vkCode == 39) && !OtherModifierHeld())
						{
							nint fg = GetForegroundWindow();
							if (fg == IntPtr.Zero)
							{
								return CallNextHookEx(_kbHook, nCode, wParam, lParam);
							}
							GetWindowThreadProcessId(fg, out var fgpid);
							if (fgpid == _ownPid)
							{
								return CallNextHookEx(_kbHook, nCode, wParam, lParam);
							}
							bool left = info.vkCode == 37;
							if (left ? (!_swallowLeftUp) : (!_swallowRightUp))
							{
								_comboConsumed = true;
								try
								{
									(left ? WinSnapLeft : WinSnapRight)?.Invoke();
								}
								catch (Exception ex)
								{
									Logger.Log("Snap raise failed: " + ex.Message);
								}
							}
							if (left)
							{
								_swallowLeftUp = true;
							}
							else
							{
								_swallowRightUp = true;
							}
							return 1;
						}
						if ((info.vkCode == 37 || info.vkCode == 39) && ShiftOnlyHeld())
						{
							nint fg2 = GetForegroundWindow();
							if (fg2 == IntPtr.Zero)
							{
								return CallNextHookEx(_kbHook, nCode, wParam, lParam);
							}
							GetWindowThreadProcessId(fg2, out var fgpid2);
							if (fgpid2 == _ownPid)
							{
								return CallNextHookEx(_kbHook, nCode, wParam, lParam);
							}
							bool left2 = info.vkCode == 37;
							if (left2 ? (!_swallowLeftUp) : (!_swallowRightUp))
							{
								_comboConsumed = true;
								try
								{
									(left2 ? WinMoveLeft : WinMoveRight)?.Invoke();
								}
								catch (Exception ex2)
								{
									Logger.Log("Move raise failed: " + ex2.Message);
								}
							}
							if (left2)
							{
								_swallowLeftUp = true;
							}
							else
							{
								_swallowRightUp = true;
							}
							return 1;
						}
					}
					else
					{
						if (isUp && _swallowCUp && info.vkCode == 67)
						{
							_swallowCUp = false;
							return 1;
						}
						if (isUp && _swallowIUp && info.vkCode == 73)
						{
							_swallowIUp = false;
							return 1;
						}
						if (isUp && _swallowEUp && info.vkCode == VK_E)
						{
							_swallowEUp = false;
							return 1;
						}
						if (isUp && _swallowTabUp && info.vkCode == 9)
						{
							_swallowTabUp = false;
							return 1;
						}
						if (isUp && _swallowSUp && info.vkCode == 83)
						{
							_swallowSUp = false;
							return 1;
						}
						if (isUp && _swallowAUp && info.vkCode == 65)
						{
							_swallowAUp = false;
							return 1;
						}
						if (isUp && _swallowLeftUp && info.vkCode == 37)
						{
							_swallowLeftUp = false;
							return 1;
						}
						if (isUp && _swallowRightUp && info.vkCode == 39)
						{
							_swallowRightUp = false;
							return 1;
						}
					}
				}
			}
		}
		return CallNextHookEx(_kbHook, nCode, wParam, lParam);
	}

	private nint MouseCallback(int nCode, nint wParam, nint lParam)
	{
		int wm = (int)wParam;
		// Charms edge-pull drag: only while armed. Pure OBSERVER — we never return 1, so the underlying window still
		// receives every move/up unchanged (no interference with scrollbars, resize borders, window drags, selection).
		if (nCode >= 0 && TrackMouseDrag && (wm == 512 || wm == 514))
		{
			MSLLHOOKSTRUCT dm = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
			if (dm.dwExtraInfo != MagicExtra)
			{
				int dx = dm.ptX;
				int dy = dm.ptY;
				Action<int, int>? dev = ((wm == 512) ? GlobalMouseMove : GlobalLeftUp);
				if (dev != null)
				{
					ThreadPool.QueueUserWorkItem(delegate { dev(dx, dy); });
				}
			}
			return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
		}
		if (nCode >= 0 && (wm == WM_LBUTTONDOWN || wm == WM_RBUTTONDOWN || wm == WM_RBUTTONUP || wm == WM_MOUSEWHEEL))
		{
			MSLLHOOKSTRUCT info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
			if (info.dwExtraInfo != MagicExtra)
			{
				int x = info.ptX;
				int y = info.ptY;
				if (wm == WM_MOUSEWHEEL)
				{
					int delta = unchecked((short)((uint)info.mouseData >> 16));
					Func<int, int, int, bool>? wheelRoute = GlobalMouseWheel;
					if (delta != 0 && wheelRoute != null)
					{
						try
						{
							if (wheelRoute(x, y, delta))
							{
								return 1;
							}
						}
						catch (Exception ex)
						{
							Logger.Log("Global mouse-wheel route failed: " + ex.Message);
						}
					}
					return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
				}
				if (wm == 517)
				{
					if (_swallowNextRUp)
					{
						_swallowNextRUp = false;
						int px = _rcX;
						int py = _rcY;
						ThreadPool.QueueUserWorkItem(delegate
						{
							ClassifyDesktopRightClick(px, py);
						});
						return 1;
					}
					return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
				}
				Action<int, int> ev = ((wm == 513) ? GlobalLeftDown : GlobalRightDown);
				if (ev != null)
				{
					ThreadPool.QueueUserWorkItem(delegate
					{
						ev(x, y);
					});
				}
				if (wm == 513 && ReplaceStartButton && !StartOpen && HitsStartButton(x, y))
				{
					Raise(StartButtonClicked);
					return 1;
				}
				if (wm == 516 && ReplaceDesktopMenu && DesktopHit.IsDesktop(x, y))
				{
					_swallowNextRUp = true;
					_rcX = x;
					_rcY = y;
					return 1;
				}
			}
		}
		return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
	}

	private void ClassifyDesktopRightClick(int x, int y)
	{
		try
		{
			if (DesktopHit.IsEmptyBackground(x, y))
			{
				DesktopRightClick?.Invoke(x, y);
				return;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Desktop RC classify: " + ex.Message);
		}
		ReinjectRightClick(x, y);
	}

	private static void ReinjectRightClick(int x, int y)
	{
		try
		{
			SetCursorPos(x, y);
			INPUT[] inputs = new INPUT[2]
			{
				MakeMouse(8u),
				MakeMouse(16u)
			};
			SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
		}
		catch (Exception ex)
		{
			Logger.Log("Reinject right-click: " + ex.Message);
		}
	}

	private static INPUT MakeMouse(uint flags)
	{
		return new INPUT
		{
			type = 0u,
			u = new InputUnion
			{
				mi = new MOUSEINPUT
				{
					dwFlags = flags,
					dwExtraInfo = MagicExtra
				}
			}
		};
	}

	private bool HitsStartButton(int x, int y)
	{
		int now = Environment.TickCount;
		if (now - _sbStamp > 2000 || _sbStamp == int.MinValue)
		{
			_sbStamp = now;
			ComputeStartButtonRects();
		}
		foreach (RECT r in _startRects)
		{
			if (x >= r.L && x < r.R && y >= r.T && y < r.B)
			{
				return true;
			}
		}
		return false;
	}

	private void ComputeStartButtonRects()
	{
		_startRects.Clear();
		AddStartRect(AppBar.PrimaryNativeTaskbar());   // explorer's taskbar, not the launcher's own Shell_TrayWnd tray host
		nint sec = IntPtr.Zero;
		while ((sec = FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
		{
			AddStartRect(sec);
		}
	}

	private void AddStartRect(nint tray)
	{
		// The launcher hides Explorer's native taskbar (AppBar.HideNativeTaskbar -> SW_HIDE), so in the normal
		// takeover case the native tray is NOT visible and contributes no rect at all — HitsStartButton then stays
		// false and there is no oversized bottom-left catcher. This is the fix for "clicking almost anywhere bottom-
		// left toggles Start": the old code widened the rect to the rebar's left edge (rb.L), which for a centered/
		// Win11 taskbar spanned the whole bottom-left and swallowed clicks meant for the Metro Start Apps down-arrow.
		// Only a genuinely visible native bar now yields a small, clamped ~40-60px Start-button box.
		if (tray == IntPtr.Zero || !IsWindowVisible(tray) || !GetWindowRect(tray, out var tr))
		{
			return;
		}
		bool horizontal = tr.R - tr.L >= tr.B - tr.T;
		RECT r;
		if (horizontal)
		{
			int right = tr.L + Math.Clamp((tr.B - tr.T) * 3 / 2, 40, 60);
			r = new RECT
			{
				L = tr.L,
				T = tr.T,
				R = right,
				B = tr.B
			};
		}
		else
		{
			int bottom = tr.T + Math.Clamp((tr.R - tr.L) * 3 / 2, 40, 60);
			r = new RECT
			{
				L = tr.L,
				T = tr.T,
				R = tr.R,
				B = bottom
			};
		}
		_startRects.Add(r);
	}

	private static bool OtherModifierHeld()
	{
		return (GetAsyncKeyState(16) & 0x8000) != 0 || (GetAsyncKeyState(18) & 0x8000) != 0 || (GetAsyncKeyState(17) & 0x8000) != 0;
	}

	private static bool ShiftOnlyHeld()
	{
		return (GetAsyncKeyState(16) & 0x8000) != 0 && (GetAsyncKeyState(18) & 0x8000) == 0 && (GetAsyncKeyState(17) & 0x8000) == 0;
	}

	private static void Raise(Action? handler)
	{
		if (handler == null)
		{
			return;
		}
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				handler();
			}
			catch (Exception value)
			{
				Logger.Log($"Hook handler failed: {value}");
			}
		});
	}

	private static void InjectCancelSequence(ushort winVk)
	{
		INPUT[] inputs = new INPUT[3]
		{
			MakeKey(17, keyUp: false),
			MakeKey(winVk, keyUp: true),
			MakeKey(17, keyUp: true)
		};
		SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
	}

	private static INPUT MakeKey(int vk, bool keyUp)
	{
		return new INPUT
		{
			type = 1u,
			u = new InputUnion
			{
				ki = new KEYBDINPUT
				{
					wVk = (ushort)vk,
					dwFlags = (keyUp ? 2u : 0u),
					dwExtraInfo = MagicExtra
				}
			}
		};
	}

	public void Dispose()
	{
		if (_threadId != 0)
		{
			PostThreadMessage(_threadId, 18u, IntPtr.Zero, IntPtr.Zero);
			_thread.Join(TimeSpan.FromSeconds(2L));
		}
		GC.SuppressFinalize(this);
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx(int idHook, LowLevelProc lpfn, nint hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(nint hhk);

	[DllImport("user32.dll")]
	private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);

	[DllImport("user32.dll")]
	private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref MSG lpMsg);

	[DllImport("user32.dll")]
	private static extern nint DispatchMessage(ref MSG lpMsg);

	[DllImport("user32.dll")]
	private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? win);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
}
