using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ClaudeStatus;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one Claude Status at a time (it also starts with Windows).
        _singleInstance = new Mutex(true, @"Local\ClaudeStatus.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled error: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("Unobserved task error: " + args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        RegisterStartWithWindows();
        new BarWindow().Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Adds Claude Status to "start when I sign in" (turn off any time in Task Manager, Startup apps).</summary>
    private static void RegisterStartWithWindows()
    {
        try
        {
            var exe = Environment.ProcessPath;
            // Only the installed copy, so a test build never takes over the startup entry.
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClaudeStatus");
            if (exe is null || !exe.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            key.SetValue("ClaudeStatus", $"\"{exe}\"");
        }
        catch (Exception ex)
        {
            Log.Write("Could not set start with Windows: " + ex.Message);
        }
    }
}
