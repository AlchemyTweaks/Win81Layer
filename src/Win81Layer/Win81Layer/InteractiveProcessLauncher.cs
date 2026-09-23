using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Win81Layer;

// The supervisor may run elevated, but the desktop shell should inherit the interactive Explorer token. On normal
// UAC-enabled systems this keeps the UI at medium integrity; on systems with EnableLUA=0 Explorer is already high.
internal static class InteractiveProcessLauncher
{
	private const uint ProcessQueryLimitedInformation = 0x1000;
	private const uint TokenAssignPrimary = 0x0001;
	private const uint TokenDuplicate = 0x0002;
	private const uint TokenQuery = 0x0008;
	private const uint MaximumAllowed = 0x02000000;
	private const uint CreateUnicodeEnvironment = 0x00000400;
	private const uint AboveNormalPriorityClass = 0x00008000;

	public static bool TryStartManagedShell(string executable, string arguments, out int processId, out string error)
	{
		processId = 0;
		error = string.Empty;
		if (!IsElevated())
		{
			try
			{
				using Process? child = Process.Start(new ProcessStartInfo(executable, arguments)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = AppContext.BaseDirectory
				});
				processId = child?.Id ?? 0;
				return child != null;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				return false;
			}
		}

		// A zero-delay logon task can beat Explorer by a few hundred milliseconds. Wait briefly for its interactive token
		// instead of falling back to an elevated UI process.
		for (int attempt = 0; attempt < 40; attempt++)
		{
			if (TryStartWithExplorerToken(executable, arguments, out processId, out error))
			{
				return true;
			}
			Thread.Sleep(100);
		}
		return false;
	}

	private static bool TryStartWithExplorerToken(string executable, string arguments, out int processId, out string error)
	{
		processId = 0;
		error = "An interactive Explorer token is not available yet.";
		int sessionId = Process.GetCurrentProcess().SessionId;
		foreach (Process explorer in Process.GetProcessesByName("explorer"))
		{
			IntPtr processHandle = IntPtr.Zero;
			IntPtr token = IntPtr.Zero;
			IntPtr primaryToken = IntPtr.Zero;
			IntPtr environment = IntPtr.Zero;
			try
			{
				if (explorer.SessionId != sessionId)
				{
					continue;
				}
				processHandle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)explorer.Id);
				if (processHandle == IntPtr.Zero)
				{
					error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
					continue;
				}
				if (!OpenProcessToken(processHandle, TokenAssignPrimary | TokenDuplicate | TokenQuery, out token))
				{
					error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
					continue;
				}
				if (!DuplicateTokenEx(token, MaximumAllowed, IntPtr.Zero, 2, 1, out primaryToken))
				{
					error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
					continue;
				}
				if (!CreateEnvironmentBlock(out environment, primaryToken, false))
				{
					environment = IntPtr.Zero;
				}

				STARTUPINFO startup = new STARTUPINFO
				{
					cb = Marshal.SizeOf<STARTUPINFO>(),
					lpDesktop = "winsta0\\default"
				};
				string commandLine = "\"" + executable.Replace("\"", string.Empty) + "\" " + arguments;
				StringBuilder mutableCommandLine = new StringBuilder(commandLine);
				bool created = CreateProcessWithTokenW(
					primaryToken,
					0,
					executable,
					mutableCommandLine,
					CreateUnicodeEnvironment | AboveNormalPriorityClass,
					environment,
					AppContext.BaseDirectory,
					ref startup,
					out PROCESS_INFORMATION info);
				if (!created)
				{
					error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
					continue;
				}
				processId = (int)info.dwProcessId;
				CloseHandle(info.hThread);
				CloseHandle(info.hProcess);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
			}
			finally
			{
				if (environment != IntPtr.Zero)
				{
					DestroyEnvironmentBlock(environment);
				}
				if (primaryToken != IntPtr.Zero)
				{
					CloseHandle(primaryToken);
				}
				if (token != IntPtr.Zero)
				{
					CloseHandle(token);
				}
				if (processHandle != IntPtr.Zero)
				{
					CloseHandle(processHandle);
				}
				explorer.Dispose();
			}
		}
		return false;
	}

	private static bool IsElevated()
	{
		try
		{
			using WindowsIdentity identity = WindowsIdentity.GetCurrent();
			WindowsPrincipal principal = new WindowsPrincipal(identity);
			return principal.IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch
		{
			return false;
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct STARTUPINFO
	{
		public int cb;
		public string? lpReserved;
		public string? lpDesktop;
		public string? lpTitle;
		public uint dwX;
		public uint dwY;
		public uint dwXSize;
		public uint dwYSize;
		public uint dwXCountChars;
		public uint dwYCountChars;
		public uint dwFillAttribute;
		public uint dwFlags;
		public short wShowWindow;
		public short cbReserved2;
		public IntPtr lpReserved2;
		public IntPtr hStdInput;
		public IntPtr hStdOutput;
		public IntPtr hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_INFORMATION
	{
		public IntPtr hProcess;
		public IntPtr hThread;
		public uint dwProcessId;
		public uint dwThreadId;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, uint processId);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

	[DllImport("userenv.dll", SetLastError = true)]
	private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

	[DllImport("userenv.dll")]
	private static extern bool DestroyEnvironmentBlock(IntPtr environment);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateProcessWithTokenW(
		IntPtr token,
		uint logonFlags,
		string applicationName,
		StringBuilder commandLine,
		uint creationFlags,
		IntPtr environment,
		string currentDirectory,
		ref STARTUPINFO startupInfo,
		out PROCESS_INFORMATION processInformation);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);
}
