using System.Windows;
using System.Windows.Threading;
using ParadisTrim.Core;
using ParadisTrim.Core.FFmpeg;
using ParadisTrim.Core.Infrastructure;
using ParadisTrim.Native;

namespace ParadisTrim;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info($"{AppInfo.ProductName} {AppInfo.Version} starting on {Environment.OSVersion}");

        // Unexpected problems are logged and explained simply, instead of the app vanishing.
        DispatcherUnhandledException += OnUnhandledUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // If Paradis Trim is ever closed abruptly, Windows stops any ffmpeg it started.
        FFmpegRunner.ProcessStarted = ChildProcessGuard.Attach;

        // Remove half-written files from a save that was interrupted last time (e.g. a power cut).
        Task.Run(TempFileJanitor.CleanUpLeftovers);

        var window = new MainWindow();
        window.Show();

        // Support "Open with" / dragging a file onto Paradis Trim.exe.
        if (e.Args.Length > 0) window.OpenFileFromCommandLine(e.Args[0]);
    }

    private void OnUnhandledUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        var message = e.Exception is FriendlyException friendly
            ? friendly.Message
            : "Something unexpected went wrong. Your original file is safe. If this keeps happening, please restart Paradis Trim.";
        MessageDialog.Inform(MainWindow, message);
    }
}
