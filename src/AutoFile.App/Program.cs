using System.Security.Cryptography;
using System.Text;
using AutoFile.Core;

namespace AutoFile.App;

static class Program
{
    /// <summary>
    /// AutoFileApp.exe              opens the window (and keeps watching in the tray when it's closed)
    /// AutoFileApp.exe --minimized  starts in the tray only (used for "Start with Windows")
    /// AutoFileApp.exe "folder"     opens the window with that folder ready to preview
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDirectory.ToUpperInvariant())))[..16];
        var showSignalName = $@"Local\AutoFileApp-show-{hash}";

        // One instance per data folder. Launching it again brings the existing window forward.
        using var instance = new Mutex(true, $@"Local\AutoFileApp-{hash}", out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (EventWaitHandle.TryOpenExisting(showSignalName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }

            return;
        }

        var startMinimized = args.Contains("--minimized");
        var folder = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && Directory.Exists(a));

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, showSignalName);
        Application.Run(new TrayContext(showSignal, startMinimized, folder));
    }
}
