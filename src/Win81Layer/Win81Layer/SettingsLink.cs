using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

#nullable enable annotations

namespace Win81Layer;

// A link to a Windows page: an ms-settings: page or shell URI first, then a program (Control Panel applet, tool) when
// the page is hidden by policy or does not exist on this PC, then nothing (the row is not shown). Parent is the page to
// use when the URI is not one of the pages Windows lists in the system index (an unverified deep link).
internal sealed record LinkSpec(string? Uri, string? File = null, string? Args = null, string? Parent = null)
{
	// "control.exe" + "/name Microsoft.PowerOptions /page pageGlobalSettings".
	internal static LinkSpec Cmd(string file, string? args = null) => new LinkSpec(null, file, args);

	internal static LinkSpec Page(string uri, string? file = null, string? args = null, string? parent = null) => new LinkSpec(uri, file, args, parent);

	internal string Describe() => Uri ?? (File + (string.IsNullOrEmpty(Args) ? "" : " " + Args));
}

// What a LinkSpec resolved to on this PC.
internal readonly record struct ResolvedLink(bool IsUri, string Target, string? Args)
{
	internal string Describe() => string.IsNullOrEmpty(Args) ? Target : Target + " " + Args;
}

// The one launch path of PC Settings and of the Windows 7 Start menu's system search: file and arguments are always
// passed separately, every launch runs on the ShellLaunch worker (in the real world) or is only recorded (QA world).
internal static class SettingsLink
{
	private static int _processStarts;

	// Real Process.Start calls requested by this process through the settings world (QA asserts 0).
	internal static int ProcessStarts => Volatile.Read(ref _processStarts);

	internal static void CountProcessStart()
	{
		Interlocked.Increment(ref _processStarts);
	}

	// Resolves a link against the capabilities of this PC; null when nothing can be opened (the row is left out).
	internal static ResolvedLink? Resolve(LinkSpec spec, HubCaps? caps)
	{
		if (spec == null)
		{
			return null;
		}
		ResolvedLink? fallback = null;
		if (!string.IsNullOrEmpty(spec.File) && CommandAllowed(spec.File!, spec.Args, caps))
		{
			fallback = new ResolvedLink(false, spec.File!, spec.Args);
		}
		string? uri = spec.Uri;
		if (string.IsNullOrEmpty(uri))
		{
			return fallback;
		}
		string? page = SettingsPageName(uri!);
		if (page != null)
		{
			if (caps != null && page.Length > 0 && !caps.PageAvailable(page))
			{
				return fallback;
			}
			if (spec.Parent != null && !IndexHasPage(uri!))
			{
				string? parentPage = SettingsPageName(spec.Parent);
				if (caps == null || parentPage == null || parentPage.Length == 0 || caps.PageAvailable(parentPage))
				{
					return new ResolvedLink(true, spec.Parent, null);
				}
				return fallback;
			}
			return new ResolvedLink(true, uri!, null);
		}
		if (uri!.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase) && caps != null && !caps.PageAvailable("windowsdefender"))
		{
			return fallback;
		}
		return new ResolvedLink(true, uri, null);
	}

	internal static bool Available(LinkSpec spec, HubCaps? caps) => Resolve(spec, caps) != null;

	// Opens the link (the caller has already checked that it resolves). false when it resolved to nothing.
	internal static bool Open(LinkSpec spec, HubCaps? caps)
	{
		ResolvedLink? r = Resolve(spec, caps);
		if (r == null)
		{
			Logger.Log("PC settings: no target for " + spec.Describe() + " on this PC");
			return false;
		}
		Run(r.Value, elevate: false);
		return true;
	}

	internal static void Run(ResolvedLink r, bool elevate)
	{
		SettingsWorld.Current.Launch(r.Target, r.Args, r.IsUri, elevate);
	}

	// The Windows 7 Start menu's system search entries (lifted from Win7StartMenu.RunHit): pages and shell names
	// through the shell as they are, programs with their arguments; runas for a task Windows marks needsElevation.
	internal static void Run(SystemHit h, bool elevate)
	{
		SettingsWorld.Current.Launch(h.Target, h.Args, h.Launch == SystemLaunchKind.Uri, elevate);
	}

	// A program with arguments (winget in a visible terminal, Explorer on a folder).
	internal static void Command(string file, string? args = null)
	{
		SettingsWorld.Current.Launch(file, args, uri: false, elevate: false);
	}

	// A URI, a folder or a document, opened through the shell as it is.
	internal static void Shell(string target)
	{
		SettingsWorld.Current.Launch(target, null, uri: true, elevate: false);
	}

	// "ms-settings:mousetouchpad?x=1" -> "mousetouchpad"; "ms-settings:" -> ""; null when it is not a Settings page.
	internal static string? SettingsPageName(string uri)
	{
		if (!uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		string rest = uri.Substring(12);
		int q = rest.IndexOf('?');
		return (q >= 0 ? rest.Substring(0, q) : rest).Trim();
	}

	// Policy gate of a program link: Control Panel applets need Control Panel (NoControlPanel, DisallowCpl, RestrictCpl);
	// tools and other programs are always allowed.
	internal static bool CommandAllowed(string file, string? args, HubCaps? caps)
	{
		if (caps == null)
		{
			return true;
		}
		string? cpl = CplKey(file, args);
		return cpl == null || caps.CplAllowed(cpl);
	}

	// The Control Panel item a command opens ("Microsoft.PowerOptions", "main.cpl"), or null when it is not one.
	internal static string? CplKey(string file, string? args)
	{
		string name = Path.GetFileName(file ?? string.Empty);
		if (name.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))
		{
			return name.ToLowerInvariant();
		}
		bool control = name.Equals("control.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("control", StringComparison.OrdinalIgnoreCase);
		bool rundll = name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) && (args ?? string.Empty).IndexOf("Control_RunDLL", StringComparison.OrdinalIgnoreCase) >= 0;
		if (!control && !rundll)
		{
			return null;
		}
		string a = (args ?? string.Empty).Trim();
		if (a.Length == 0)
		{
			return "control.exe";
		}
		int n = a.IndexOf("/name ", StringComparison.OrdinalIgnoreCase);
		if (n >= 0)
		{
			string rest = a.Substring(n + 6).Trim();
			int sp = rest.IndexOf(' ');
			return sp >= 0 ? rest.Substring(0, sp) : rest;
		}
		int cpl = a.IndexOf(".cpl", StringComparison.OrdinalIgnoreCase);
		if (cpl >= 0)
		{
			int start = a.LastIndexOfAny(new[] { ' ', ',' }, cpl) + 1;
			return a.Substring(start, cpl + 4 - start).ToLowerInvariant();
		}
		return a.Split(' ')[0].ToLowerInvariant() switch
		{
			"printers" => "Microsoft.DevicesAndPrinters",
			"keyboard" => "Microsoft.Keyboard",
			"mouse" => "Microsoft.Mouse",
			"folders" => "Microsoft.FolderOptions",
			"userpasswords" or "userpasswords2" => "Microsoft.UserAccounts",
			"fonts" => "Microsoft.Fonts",
			_ => "control.exe"
		};
	}

	private static int _indexRevision = -1;

	private static HashSet<string>? _indexPages;

	// The Settings page URIs Windows itself lists (from the system index, once it is complete); a URI that is not among
	// them falls back to its parent page. Before the index is ready every URI is taken as it is.
	internal static bool IndexHasPage(string uri)
	{
		SystemSearchIndex? index = SystemSearchIndex.Current;
		if (index == null || !index.Complete)
		{
			return true;
		}
		if (_indexPages == null || _indexRevision != SystemSearchIndex.Revision)
		{
			HashSet<string> pages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (SystemHit h in index.Hits)
			{
				if (h.Launch == SystemLaunchKind.Uri && h.Target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
				{
					string t = h.Target;
					int q = t.IndexOf('?');
					pages.Add(q >= 0 ? t.Substring(0, q) : t);
				}
			}
			_indexPages = pages;
			_indexRevision = SystemSearchIndex.Revision;
		}
		int qi = uri.IndexOf('?');
		return _indexPages.Contains(qi >= 0 ? uri.Substring(0, qi) : uri);
	}
}
