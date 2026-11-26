using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using LolConfigSync.App.Controls;
using LolConfigSync.App.Platform;
using LolConfigSync.App.ViewModels;
using LolConfigSync.Core.Hosting;
using LolConfigSync.Core.Lcu;
using LolConfigSync.Core.Storage;
using Microsoft.Win32;

namespace LolConfigSync.App;

public partial class App : Application, IAppShell
{
    private SingleInstance? _instance;
    private AppHost? _host;
    private AppDataStore _store = null!;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire(() => Dispatcher.BeginInvoke(ShowMainWindow));
        if (_instance is null)
        {
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

        _store = new AppDataStore(AppDataStore.DefaultRoot);
        FileLog.Init(Path.Combine(_store.RootDir, "log.txt"));
        DispatcherUnhandledException += (_, args) =>
        {
            FileLog.Error("Unhandled UI exception", args.Exception);
            ShowError(args.Exception);
            args.Handled = true;
        };

        var installDir = ResolveInstallDir();
        if (installDir is null)
        {
            Quit();
            return;
        }

        var settings = _store.LoadSettings();
        if (Environment.ProcessPath is { } exe)
        {
            try { StartupRegistration.Apply(settings.StartWithWindows, exe); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { FileLog.Error("Startup registration failed", ex); }
        }

        _host = new AppHost(_store, installDir, TimeProvider.System);
        _host.Engine.FirstRunPrompt = account => Dispatcher.InvokeAsync(() => HexDialog.Show(_window,
            "Use this account as your main setup?",
            $"{account.RiotId}'s keybinds, interface, camera and sound settings become the setup every account gets. " +
            "You can change it any time from the editor or pick another account later.",
            "Use as main", "Not this one")).Task;
        _host.Engine.Notify += message => Dispatcher.BeginInvoke(() => Notify(message));

        _vm = new MainViewModel(_host, _store, Dispatcher, this);
        _window = new MainWindow(_vm);
        CreateTray();

        if (!e.Args.Contains("--tray")) ShowMainWindow();
        await _host.StartAsync();
    }

    private string? ResolveInstallDir()
    {
        var settings = _store.LoadSettings();
        var dir = settings.InstallDirOverride is { } custom && Directory.Exists(custom)
            ? custom
            : LeagueInstallLocator.LocateOnThisMachine();
        if (dir is not null) return dir;

        if (!HexDialog.Show(null, "Where is League installed?",
                "League of Legends wasn't found in its usual place. Choose the League of Legends folder (the one with LeagueClient.exe in it).",
                "Choose folder", "Quit"))
        {
            return null;
        }

        while (true)
        {
            var picker = new OpenFolderDialog { Title = "Choose the League of Legends folder" };
            if (picker.ShowDialog() != true) return null;
            if (File.Exists(Path.Combine(picker.FolderName, "LeagueClient.exe")) || Directory.Exists(Path.Combine(picker.FolderName, "Config")))
            {
                settings.InstallDirOverride = picker.FolderName;
                _store.SaveSettings(settings);
                return picker.FolderName;
            }
            if (!HexDialog.Show(null, "That isn't the League folder",
                    "The folder needs to contain LeagueClient.exe. It's usually called \"League of Legends\" inside \"Riot Games\".",
                    "Try again", "Quit"))
            {
                return null;
            }
        }
    }

    private void CreateTray()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("HexContextMenu") };
        menu.Items.Add(MenuItem("Open LoL Config Sync", ShowMainWindow));
        menu.Items.Add(MenuItem("Sync now", () => _vm?.SyncNowCommand.Execute(null)));
        menu.Items.Add(MenuItem("Quit", Quit));

        _tray = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ToolTipText = "LoL Config Sync",
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowMainWindow),
        };
        _tray.ForceCreate(enablesEfficiencyMode: false);
        if (_host is not null)
        {
            _host.Engine.StatusChanged += _ => Dispatcher.BeginInvoke(() =>
            {
                if (_tray is not null && _vm is not null) _tray.ToolTipText = $"LoL Config Sync\n{_vm.Headline}";
            });
        }
    }

    private MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource("HexMenuItem") };
        item.Click += (_, _) => action();
        return item;
    }

    private void Notify(string message)
    {
        if (_tray is null || !_store.LoadSettings().ShowNotifications) return;
        _tray.ShowNotification("LoL Config Sync", message);
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _vm?.Refresh();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void Quit()
    {
        if (_window is not null)
        {
            _window.HideOnClose = false;
            _window.Close();
        }
        _tray?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
        _instance?.Dispose();
        Shutdown();
    }

    // ---- IAppShell ----

    public bool Confirm(string title, string message, string primary, string secondary) =>
        HexDialog.Show(_window, title, message, primary, secondary);

    public void ShowError(Exception e)
    {
        var message = e is InvalidOperationException ? e.Message : $"{e.Message}\n\nDetails are in {Path.Combine(_store.RootDir, "log.txt")}.";
        HexDialog.Show(_window, "That didn't work", message);
    }

    public void ShowEditor() => HexDialog.Show(_window, "Editor", "The editor arrives in the next build step.");

    public void ShowBackups() => HexDialog.Show(_window, "Backups", "Backups arrive in the next build step.");
}
