using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Win81Layer;

public static class LogonTask
{
	private const string TaskName = "Win81Layer Logon";

	public const string TaskSchemaMarker = "Win81Layer-TaskSchema-2026.08.29-v3";

	public static string CanonicalExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Win81Metro-Ready", "Launcher-x64", "Win81Layer.exe");

	public static bool IsEnabled()
	{
		try
		{
			return Run("/Query", "/TN", "Win81Layer Logon") == 0;
		}
		catch
		{
			return false;
		}
	}

	public static bool SetEnabled(bool enabled)
	{
		try
		{
			if (!enabled)
			{
				int del = Run("/Delete", "/TN", "Win81Layer Logon", "/F");
				Logger.Log($"Logon task delete rc={del}");
				return del == 0 || !IsEnabled();
			}
			if (IsCanonical())
			{
				return true;
			}
			string tmp = Path.Combine(Path.GetTempPath(), "win81layer_logon.xml");
			File.WriteAllText(tmp, BuildXml(), Encoding.Unicode);
			int rc = Run("/Create", "/TN", "Win81Layer Logon", "/XML", tmp, "/F");
			try
			{
				File.Delete(tmp);
			}
			catch
			{
			}
			Logger.Log($"Logon task ensured → canonical build ({CanonicalExe}), rc={rc}");
			return rc == 0;
		}
		catch (Exception ex)
		{
			Logger.Log($"LogonTask.SetEnabled({enabled}): {ex.Message}");
			return false;
		}
	}

	public static bool IsCanonical()
	{
		try
		{
			string? xml = CurrentDefinition();
			return !string.IsNullOrWhiteSpace(xml)
				&& string.Equals(ElementValue(xml!, "Command"), CanonicalExe, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(ElementValue(xml!, "Arguments"), "--supervisor", StringComparison.OrdinalIgnoreCase)
				&& string.IsNullOrWhiteSpace(ElementValue(xml!, "Delay"))
				&& string.Equals(ElementValue(xml!, "Priority"), "2", StringComparison.OrdinalIgnoreCase)
				&& string.Equals(ElementValue(xml!, "RunLevel"), "HighestAvailable", StringComparison.OrdinalIgnoreCase)
				&& xml!.Contains("<BootTrigger", StringComparison.Ordinal)
				&& xml!.Contains(TaskSchemaMarker, StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	public static string? CurrentCommand()
	{
		string? xml = CurrentDefinition();
		return string.IsNullOrWhiteSpace(xml) ? null : ElementValue(xml!, "Command");
	}

	public static string? CurrentDefinition()
	{
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			string[] array = new string[4] { "/Query", "/TN", "Win81Layer Logon", "/XML" };
			foreach (string a in array)
			{
				psi.ArgumentList.Add(a);
			}
			using Process p = Process.Start(psi);
			if (p == null)
			{
				return null;
			}
			string outp = p.StandardOutput.ReadToEnd();
			p.StandardError.ReadToEnd();
			p.WaitForExit(8000);
			return p.ExitCode == 0 ? outp : null;
		}
		catch
		{
			return null;
		}
	}

	private static string BuildXml()
	{
		string user;
		try
		{
			user = WindowsIdentity.GetCurrent().Name;
		}
		catch
		{
			user = Environment.UserDomainName + "\\" + Environment.UserName;
		}
		// --supervisor: the Task launches a tiny, UI-less, COM-less supervisor process instead of the shell directly.
		// The supervisor Process.Start-launches the shell (--autostart --managed), verifies a fresh advancing heartbeat,
		// and RETRIES until one comes up healthy — then monitors and relaunches on wedge/crash/freeze. This is the
		// definitive cure for the boot-wedge: a task-directly-launched SHELL could throw in a shell-COM AppsFolder call
		// during the boot storm and hang with no window/heartbeat (dropping the user's saved layout). The supervisor
		// itself does none of that, so it can never wedge, and any shell that fails to come up is simply relaunched.
		// The Boot trigger arms the task with Task Scheduler startup, while Logon guarantees an interactive desktop.
		// There is intentionally no Logon delay. Priority 2 maps to ABOVE_NORMAL for the tiny supervisor. The
		// supervisor then launches the WPF shell with Explorer's interactive token. That is medium integrity on normal
		// UAC-enabled systems and high only when Windows is already configured with EnableLUA=0.
		string escapedUser = System.Security.SecurityElement.Escape(user) ?? string.Empty;
		string escapedExe = System.Security.SecurityElement.Escape(CanonicalExe) ?? string.Empty;
		return $"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n  <RegistrationInfo><Description>{TaskSchemaMarker}: boot-armed, zero-delay, elevated supervisor with interactive-user shell</Description></RegistrationInfo>\n  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger><LogonTrigger><Enabled>true</Enabled><UserId>{escapedUser}</UserId></LogonTrigger></Triggers>\n  <Principals><Principal id=\"Author\"><UserId>{escapedUser}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n  <Settings>\n    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n    <AllowHardTerminate>true</AllowHardTerminate>\n    <StartWhenAvailable>true</StartWhenAvailable>\n    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\n    <AllowStartOnDemand>true</AllowStartOnDemand>\n    <Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>\n    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>2</Priority>\n  </Settings>\n  <Actions Context=\"Author\"><Exec><Command>{escapedExe}</Command><Arguments>--supervisor</Arguments></Exec></Actions>\n</Task>";
	}

	private static string? ElementValue(string xml, string localName)
	{
		XDocument document = XDocument.Parse(xml, LoadOptions.None);
		foreach (XElement element in document.Descendants())
		{
			if (element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
			{
				return element.Value.Trim();
			}
		}
		return null;
	}

	private static int Run(params string[] args)
	{
		ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (string a in args)
		{
			psi.ArgumentList.Add(a);
		}
		using Process p = Process.Start(psi);
		if (p == null)
		{
			return -1;
		}
		p.StandardOutput.ReadToEnd();
		p.StandardError.ReadToEnd();
		p.WaitForExit(8000);
		return p.HasExited ? p.ExitCode : (-1);
	}
}
