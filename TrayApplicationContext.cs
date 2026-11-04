using System.Reflection;
using Microsoft.Win32;

namespace TheWitness;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan FinalReportTimeout = TimeSpan.FromSeconds(10);

    private readonly Icon appIcon;
    private readonly NotifyIcon trayIcon;
    private readonly TaskbarCreatedWatcher taskbarWatcher;

    internal TrayApplicationContext()
    {
        ToolStripMenuItem indicatorMenuItem = new("Показывать индикатор")
        {
            CheckOnClick = true,
            Checked = true
        };
        indicatorMenuItem.CheckedChanged += (_, _) => SessionIndicatorOverlay.SetUserVisible(indicatorMenuItem.Checked);

        SessionIndicatorOverlay.UserVisibilityChanged += visible =>
            UiDispatcher.InvokeAction(() => indicatorMenuItem.Checked = visible);

        ContextMenuStrip menu = new();
        menu.Items.Add(indicatorMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => BeginShutdown());

        appIcon = LoadAppIcon();
        trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "The Witness",
            ContextMenuStrip = menu,
            Visible = true
        };

        taskbarWatcher = new TaskbarCreatedWatcher(ShowTrayIcon);

        System.Windows.Forms.Timer trayIconNudgeTimer = new() { Interval = 3000 };
        trayIconNudgeTimer.Tick += (_, _) =>
        {
            trayIconNudgeTimer.Stop();
            trayIconNudgeTimer.Dispose();
            ShowTrayIcon();
        };
        trayIconNudgeTimer.Start();

        SystemEvents.SessionEnding += (_, _) => BeginShutdown();

        UiDispatcher.Initialize();
        SessionIndicatorOverlay.Initialize();

        Task.Run(WitnessMonitor.Run);
    }

    private void ShowTrayIcon()
    {
        trayIcon.Visible = false;
        trayIcon.Visible = true;
    }

    private void BeginShutdown()
    {
        trayIcon.Visible = false;
        SessionIndicatorOverlay.Shutdown();
        ShutdownSignal.Request();

        ShutdownSignal.WaitForFinished(FinalReportTimeout);

        taskbarWatcher.Dispose();
        trayIcon.Dispose();

        if (!ReferenceEquals(appIcon, SystemIcons.Shield))
        {
            appIcon.Dispose();
        }

        ExitThread();
    }

    private static Icon LoadAppIcon()
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TheWitness.AppIcon.ico");
        return stream is null ? SystemIcons.Shield : new Icon(stream);
    }
}
