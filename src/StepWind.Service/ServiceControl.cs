using System.Diagnostics;
using System.Runtime.Versioning;

namespace StepWind.Service;

/// <summary>
/// Registers/unregisters the StepWind Windows service. Invoked by the installer (which runs
/// elevated) via the service exe's own verbs, so all the service-plumbing logic lives in our
/// code rather than being wrangled as sc.exe strings inside the installer script.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public const string ServiceName = "StepWind";

    public static int Install()
    {
        string exe = Environment.ProcessPath!;
        Sc("stop", ServiceName);         // ignore failure if not present
        Sc("delete", ServiceName);
        Thread.Sleep(500);

        int rc = Sc("create", ServiceName, "binPath=", $"\"{exe}\"", "start=", "auto",
            "DisplayName=", "StepWind Protection", "obj=", "LocalSystem");
        if (rc != 0)
        {
            return rc;
        }

        Sc("description", ServiceName,
            "Real-time file protection and version history (USN + ETW flight recorder).");
        // Auto-restart on crash: 5s, 5s, 5s; reset the counter daily.
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000");
        Sc("start", ServiceName);
        return 0;
    }

    public static int Uninstall()
    {
        Sc("stop", ServiceName);
        Thread.Sleep(1500);
        return Sc("delete", ServiceName);
    }

    public static int Start() => Sc("start", ServiceName);

    public static int Stop() => Sc("stop", ServiceName);

    private static int Sc(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }

            p.WaitForExit(30000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
