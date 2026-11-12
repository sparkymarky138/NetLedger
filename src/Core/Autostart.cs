using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace NetLedger.Core;

/// <summary>
/// "Start when I sign in" through a Task Scheduler task with highest privileges, so the
/// admin-only exe starts without a UAC prompt. (A Run registry key cannot do that.)
/// </summary>
public static class Autostart
{
    public const string TaskName = "NetLedger";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    public static void Enable()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the program path.");
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Starts the NetLedger network monitor in the tray when you sign in.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--minimized</Arguments></Exec></Actions>
</Task>";
        string tmp = Path.Combine(Path.GetTempPath(), "netledger-task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            if (RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F", out var output) != 0)
                throw new InvalidOperationException("Task Scheduler refused the task: " + output.Trim());
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static void Disable()
    {
        if (!IsEnabled()) return;
        if (RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out var output) != 0)
            throw new InvalidOperationException("Could not remove the sign-in task: " + output.Trim());
    }

    static int RunSchtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }
}
