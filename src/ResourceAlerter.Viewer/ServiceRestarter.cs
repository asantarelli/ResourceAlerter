using System.Diagnostics;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Viewer;

/// <summary>
/// The Viewer runs as a regular user and can't control a LocalSystem service directly, so this
/// launches an elevated PowerShell one-liner (same "shell out, elevated, check exit code"
/// pattern as the send-summary button) rather than requiring the whole Viewer to run as admin.
/// Restart-Service handles the stop-then-start sequencing atomically, avoiding a manual
/// stop/sleep/start race.
/// </summary>
internal static class ServiceRestarter
{
    private const string ServiceName = "ResourceAlerter";

    public static async Task<bool> RestartElevatedAsync(IWin32Window owner)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"Restart-Service -Name {ServiceName} -Force\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException(Strings.Viewer_StartProcessFailed);
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                MessageBox.Show(owner, Strings.Viewer_ServiceRestartedOk,
                    "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

            MessageBox.Show(owner,
                Strings.Viewer_ServiceRestartFailed(ServiceName),
                "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User declined the UAC elevation prompt — the config is already saved either way.
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, Strings.Viewer_ServiceRestartError(ex.Message),
                "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}
