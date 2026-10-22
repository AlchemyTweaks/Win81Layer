#nullable enable

namespace Win81Layer;

// Tells a low-level hook that Windows removed (a callback overran LowLevelHooksTimeout: the Win key, the Start button,
// light dismiss or the hot corners would stop) from one that merely saw no input. Fed once per shell clock tick with
// what the system saw since the previous tick (the cursor position, GetLastInputInfo) and what each hook saw (its event
// count). Pure logic: the caller supplies every observation (tested by --perfprobe unit).
//   Mouse hook dead: the cursor moved, the system saw input, and the mouse hook saw no event, on two ticks in a row.
//     SetCursorPos alone moves the cursor without input, so it never counts.
//   Keyboard hook dead: the system saw input with the cursor still and neither hook saw an event, on two ticks in a row.
//   Never counted (excused): the first seconds after unlock or display-on (the input went to the lock screen), the
//   cursor over or the foreground on a window whose input is hidden from our hooks (a higher integrity level, or no
//   foreground at all: the secure desktop), and a remote-desktop or VM client in front (it swallows input with its own
//   later hook by design; re-installing ours would put it in front of theirs).
// Each hook is re-installed on its own, at most every 30 s.
internal sealed class InputHookLiveness
{
	internal const int StrikesNeeded = 2;

	internal const long MinGapMs = 30000L;

	internal const long GraceMs = 5000L;

	internal struct Sample
	{
		public long Now;

		public bool HaveCursor;   // false: GetCursorPos failed (the secure desktop or the lock screen has the input)

		public int X;

		public int Y;

		public int MouseEvents;

		public int KeyEvents;

		public long InputIdleMs;
	}

	private bool _prevValid;

	private long _prevNow;

	private int _prevX;

	private int _prevY;

	private int _prevMouse;

	private int _prevKeys;

	private int _mouseStrikes;

	private int _keyStrikes;

	private long _graceUntil = long.MinValue / 2;

	private long _lastMouseReinstall = long.MinValue / 2;

	private long _lastKeyReinstall = long.MinValue / 2;

	// What the last Observe saw (read by Judge).
	private bool _silentMove;

	private bool _silentKeys;

	// The cursor moved since the previous tick, the system saw input, and the mouse hook saw no event.
	internal bool SilentMove => _silentMove;

	// The system saw input since the previous tick with the cursor still, and neither hook saw an event.
	internal bool SilentKeys => _silentKeys;

	internal void Reset()
	{
		_prevValid = false;
		_mouseStrikes = 0;
		_keyStrikes = 0;
		_silentMove = false;
		_silentKeys = false;
	}

	// Unlock, display-on: input typed on the lock screen never reached the hooks.
	internal void Grace(long now)
	{
		_graceUntil = now + GraceMs;
		Reset();
	}

	// Step 1: compare this tick with the previous one. Returns true when anything looked silent (only then does the
	// caller need to look at the windows for an excuse, which may open processes).
	internal bool Observe(in Sample s)
	{
		if (!s.HaveCursor)
		{
			Reset();
			return false;
		}
		bool havePrev = _prevValid;
		long interval = s.Now - _prevNow;
		bool moved = havePrev && (s.X != _prevX || s.Y != _prevY);
		bool mouseQuiet = havePrev && s.MouseEvents == _prevMouse;
		bool keysQuiet = havePrev && s.KeyEvents == _prevKeys;
		bool systemInput = havePrev && interval > 0 && s.InputIdleMs < interval;
		_prevValid = true;
		_prevNow = s.Now;
		_prevX = s.X;
		_prevY = s.Y;
		_prevMouse = s.MouseEvents;
		_prevKeys = s.KeyEvents;
		_silentMove = moved && mouseQuiet && systemInput;
		_silentKeys = !moved && mouseQuiet && keysQuiet && systemInput;
		return _silentMove || _silentKeys;
	}

	// Step 2 (after Observe): which hooks to re-install. excused: one of the cases above holds; active: the clock is in
	// its Active mode (the checks run only there).
	internal (bool Keyboard, bool Mouse) Judge(long now, bool excused, bool active)
	{
		bool countable = active && !excused && now >= _graceUntil;
		bool mouse = false;
		bool keyboard = false;
		if (countable && _silentMove)
		{
			if (++_mouseStrikes >= StrikesNeeded && now - _lastMouseReinstall >= MinGapMs)
			{
				_mouseStrikes = 0;
				_lastMouseReinstall = now;
				mouse = true;
			}
		}
		else
		{
			_mouseStrikes = 0;
		}
		if (countable && _silentKeys)
		{
			if (++_keyStrikes >= StrikesNeeded && now - _lastKeyReinstall >= MinGapMs)
			{
				_keyStrikes = 0;
				_lastKeyReinstall = now;
				keyboard = true;
			}
		}
		else
		{
			_keyStrikes = 0;
		}
		return (keyboard, mouse);
	}
}
