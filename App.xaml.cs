using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AppLauncher.Dialogs;
using AppLauncher.Services;
using Forms = System.Windows.Forms;

namespace AppLauncher;

public partial class App : Application
{
    private const string MutexName = @"Local\Serhii_AppLauncher_SingleInstance";
    private const string EventName = @"Local\Serhii_AppLauncher_Show";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _listenerCancellation;
    private Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayDrawingIcon;
    private TrayMenuWindow? _trayMenu;
    private bool _isExiting;
    private int _fatalErrorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterCrashHandlers();

        try
        {
            _mutex = new Mutex(true, MutexName, out var isFirstInstance);
            _ownsMutex = isFirstInstance;
            if (!isFirstInstance)
            {
                try
                {
                    using var existingEvent = EventWaitHandle.OpenExisting(EventName);
                    existingEvent.Set();
                }
                catch
                {
                    // Первый экземпляр мог завершаться именно в этот момент.
                }

                Shutdown();
                return;
            }

            base.OnStartup(e);

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _listenerCancellation = new CancellationTokenSource();
            StartActivationListener(_listenerCancellation.Token);

            var window = new MainWindow();
            MainWindow = window;
            InitializeTrayIcon();
            window.Show();
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _fatalErrorShown, 1) == 0)
                CrashReporter.ReportAndShow(ex, "Ошибка запуска AppLauncher");
            if (MainWindow is MainWindow launcher)
                launcher.PrepareForApplicationExit();
            Shutdown(-1);
        }
    }

    private void InitializeTrayIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
            _trayDrawingIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "AppLauncher",
            Icon = _trayDrawingIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(ShowMainWindow);
            else if (args.Button == Forms.MouseButtons.Right)
                Dispatcher.BeginInvoke(ShowTrayMenu);
        };
    }

    private void ShowTrayMenu()
    {
        if (_trayMenu is { IsVisible: true })
        {
            _trayMenu.Activate();
            return;
        }

        _trayMenu = new TrayMenuWindow(ShowMainWindow, ExitFromTray, HideInactiveMainWindow);
        _trayMenu.Closed += (_, _) => _trayMenu = null;
        _trayMenu.Show();
        _trayMenu.Activate();
    }

    private void ShowMainWindow()
    {
        _trayMenu?.Close();
        if (MainWindow is MainWindow launcher)
            launcher.ShowLauncher();
    }

    private void HideInactiveMainWindow()
    {
        if (MainWindow is MainWindow launcher)
            launcher.HideIfUnpinnedAndInactive();
    }

    private void ExitFromTray()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        if (MainWindow is MainWindow launcher)
            launcher.PrepareForApplicationExit();
        Shutdown();
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                CrashReporter.Write(exception, "Необработанная ошибка AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashReporter.Write(args.Exception, "Необработанная ошибка фоновой задачи");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (Interlocked.Exchange(ref _fatalErrorShown, 1) != 0)
            return;

        CrashReporter.ReportAndShow(e.Exception, "Ошибка AppLauncher");
        if (MainWindow is MainWindow launcher)
            launcher.PrepareForApplicationExit();
        Shutdown(-1);
    }

    private void StartActivationListener(CancellationToken cancellationToken)
    {
        var showEvent = _showEvent!;
        Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!showEvent.WaitOne(500))
                    continue;

                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is MainWindow launcher)
                        launcher.ShowLauncher();
                });
            }
        }, cancellationToken);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayMenu?.Close();
        _trayDrawingIcon?.Dispose();
        _listenerCancellation?.Cancel();
        _showEvent?.Set();
        _showEvent?.Dispose();
        if (_ownsMutex)
            _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
