using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LolConfigSync.App.Platform;
using LolConfigSync.Core.Hosting;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Presentation;
using LolConfigSync.Core.Storage;
using LolConfigSync.Core.Sync;
using LolConfigSync.Core.Updates;

namespace LolConfigSync.App.ViewModels;

public sealed class SectionToggleViewModel(SyncSection section, string title, string description, bool enabled, Action<SyncSection, bool> onChanged)
    : ObservableObject
{
    private bool _enabled = enabled;

    public SyncSection Section { get; } = section;
    public string Title { get; } = title;
    public string Description { get; } = description;

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value)) onChanged(Section, value);
        }
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly AppDataStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private AppSettings _settings;
    private StatusView _view;
    private string _mainSetupInfo = "";
    private bool _startWithWindows;

    public MainViewModel(AppHost host, AppDataStore store, Dispatcher dispatcher, IAppShell shell)
    {
        _host = host;
        _store = store;
        _dispatcher = dispatcher;
        _settings = store.LoadSettings();
        _startWithWindows = _settings.StartWithWindows;
        _view = StatusText.Describe(host.Engine.Status, store.LoadMainSetup() is not null, DateTimeOffset.Now);

        AccountSections =
        [
            Toggle(SyncSection.Hotkeys, "Hotkeys", "Abilities, items, camera and quick-cast keys"),
            Toggle(SyncSection.Interface, "Interface", "HUD and minimap size, chat, health bars"),
            Toggle(SyncSection.Game, "Game", "Camera, mouse speed, targeting, cursor"),
            Toggle(SyncSection.Sound, "Sound", "Volume levels for music, effects and voice"),
        ];
        ExtraSections =
        [
            Toggle(SyncSection.RunePages, "Rune pages", "Your own pages. One slot always stays free for Porofessor"),
            Toggle(SyncSection.ItemSets, "Item sets", "Custom item sets"),
            Toggle(SyncSection.ClientSettings, "Client settings", "Champ select, party and notification preferences"),
            Toggle(SyncSection.Video, "Graphics", "Resolution, window mode and quality on this PC"),
        ];

        Action<Exception> onError = e => shell.ShowError(e);
        SyncNowCommand = new RelayCommand(() => _host.Engine.PushMainSetupAsync(CancellationToken.None), () => HasAccount && HasMainSetup, onError);
        UseThisAccountCommand = new RelayCommand(async () =>
        {
            var riotId = _host.Engine.Status.Account?.RiotId;
            if (!shell.Confirm("Use this account as main?",
                    $"Your main setup will be replaced with {riotId}'s current settings. Every account you log into after this gets them.",
                    "Use as main", "Cancel"))
            {
                return;
            }
            await _host.Engine.AdoptCurrentAccountAsMainAsync(CancellationToken.None);
        }, () => HasAccount, onError);
        EditCommand = new RelayCommand(() => { shell.ShowEditor(); ReloadSettings(); }, () => HasMainSetup);
        BackupsCommand = new RelayCommand(shell.ShowBackups);
        OpenDataFolderCommand = new RelayCommand(() => shell.OpenFolder(_store.RootDir));
        _shell = shell;
        CheckForUpdatesCommand = new RelayCommand(() => CheckForUpdatesAsync(userAsked: true), () => !IsUpdating, onError);
        AboutCommand = new RelayCommand(() => shell.ShowMessage("About LoL Config Sync",
            $"Version {CurrentVersion.ToString(3)}. Free and open source under the MIT License.\n" +
            $"Source, releases and issues: github.com/{UpdateSource.Owner}/{UpdateSource.Repo}\n\n" +
            "LoL Config Sync isn't endorsed by Riot Games and doesn't reflect the views or opinions of Riot Games or anyone " +
            "officially involved in producing or managing Riot Games properties. Riot Games, and all associated properties " +
            "are trademarks or registered trademarks of Riot Games, Inc."));
        UpdateNowCommand = new RelayCommand(UpdateNowAsync, () => AvailableUpdate is not null && !IsUpdating, onError);

        _host.Engine.StatusChanged += _ => _dispatcher.BeginInvoke(Refresh);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
        _clock.Start();
        Refresh();
    }

    public IReadOnlyList<SectionToggleViewModel> AccountSections { get; }
    public IReadOnlyList<SectionToggleViewModel> ExtraSections { get; }
    public ObservableCollection<string> Warnings { get; } = [];

    public ICommand SyncNowCommand { get; }
    public ICommand UseThisAccountCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand BackupsCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    /// <summary>Where the main setup, settings and backups are stored.</summary>
    public string DataFolder => _store.RootDir;

    // ---- updates ----
    private readonly IAppShell _shell;
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private ReleaseInfo? _availableUpdate;
    private bool _isUpdating;
    private string _updateProgress = "";

    public ICommand CheckForUpdatesCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand UpdateNowCommand { get; }

    public static Version CurrentVersion { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    public string VersionText => $"Version {CurrentVersion.ToString(3)}";

    public ReleaseInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (!Set(ref _availableUpdate, value)) return;
            Raise(nameof(HasUpdate));
            Raise(nameof(UpdateHeadline));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasUpdate => _availableUpdate is not null;
    public string UpdateHeadline => _availableUpdate is null ? "" : $"Version {_availableUpdate.Version.ToString(3)} is available";

    public bool IsUpdating
    {
        get => _isUpdating;
        private set => Set(ref _isUpdating, value);
    }

    public string UpdateProgress
    {
        get => _updateProgress;
        private set => Set(ref _updateProgress, value);
    }

    /// <summary>Automatic check: at most once a day, quietly; shows the banner (and a toast) when there's something new.</summary>
    public async Task CheckOnStartupAsync()
    {
        var settings = _store.LoadSettings();
        if (!settings.CheckForUpdates || !UpdateChecker.IsCheckDue(settings.LastUpdateCheck, DateTimeOffset.UtcNow)) return;
        try
        {
            await CheckForUpdatesAsync(userAsked: false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            FileLog.Error("Automatic update check failed", e);
        }
    }

    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        var release = await new UpdateChecker(UpdateHttp, UpdateSource.Owner, UpdateSource.Repo).GetLatestAsync(CancellationToken.None);
        var settings = _store.LoadSettings();
        settings.LastUpdateCheck = DateTimeOffset.UtcNow;
        _store.SaveSettings(settings);

        if (release is not null && UpdateChecker.IsNewer(CurrentVersion, release.Version))
        {
            AvailableUpdate = release;
            if (userAsked) await UpdateNowAsync();
            else _shell.Notify($"{UpdateHeadline}. Open LoL Config Sync to update.");
        }
        else if (userAsked)
        {
            _shell.ShowMessage("You're up to date", $"LoL Config Sync {CurrentVersion.ToString(3)} is the latest version.");
        }
    }

    private async Task UpdateNowAsync()
    {
        var release = AvailableUpdate;
        if (release is null) return;
        var notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : $"\n\nWhat's new:\n{release.Notes.Trim()}";
        if (!_shell.Confirm($"Update to version {release.Version.ToString(3)}?",
                $"The app downloads the update, restarts, and keeps your setup, settings and backups.{notes}",
                "Update now", "Later"))
        {
            return;
        }

        IsUpdating = true;
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the running program to update.");
            var progress = new Progress<string>(text => UpdateProgress = text);
            if (await new Updater(UpdateHttp).UpdateAsync(release, exe, progress, CancellationToken.None)) _shell.ExitForUpdate();
        }
        finally
        {
            IsUpdating = false;
            UpdateProgress = "";
        }
    }

    // ---- who's logged in ----
    private ImageSource? _accountIcon;
    private (string Puuid, int Icon)? _iconFor;
    private bool _iconLoading;
    private long _iconRequest;

    public string AccountName => _host.Engine.Status.Account?.RiotId ?? "Nobody logged in";

    public string AccountDetail => _host.Engine.Status.Account is { } a
        ? (a.SummonerLevel > 0 ? $"Level {a.SummonerLevel}" : "Logged in")
        : _host.Engine.Status.State == SyncState.WaitingForClient ? "The League client isn't open" : "Log into the League client";

    /// <summary>First letter of the Riot ID, shown until (or instead of) the profile icon.</summary>
    public string AccountInitial => _host.Engine.Status.Account?.RiotId is { Length: > 0 } id ? id[..1].ToUpperInvariant() : "?";

    public ImageSource? AccountIcon
    {
        get => _accountIcon;
        private set
        {
            if (Set(ref _accountIcon, value)) Raise(nameof(HasAccountIcon));
        }
    }

    public bool HasAccountIcon => _accountIcon is not null;

    private void RefreshAccountIcon()
    {
        var account = _host.Engine.Status.Account;
        var key = account is null || account.ProfileIconId <= 0 ? ((string, int)?)null : (account.Puuid, account.ProfileIconId);
        if (key == _iconFor)
        {
            // The first request can fail while League is still logging in; try again on the next refresh.
            if (key is { } pending && AccountIcon is null && !_iconLoading) LoadAccountIcon(pending);
            return;
        }
        _iconFor = key;
        _iconRequest++;
        _iconLoading = false;
        AccountIcon = null;
        if (key is { } current) LoadAccountIcon(current);
    }

    private async void LoadAccountIcon((string Puuid, int Icon) key)
    {
        var request = _iconRequest;
        _iconLoading = true;
        try
        {
            var bytes = await _host.GetProfileIconAsync(key.Icon);
            if (bytes is null || request != _iconRequest) return;
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();
            AccountIcon = image;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or FileFormatException)
        {
            FileLog.Error($"Could not decode League profile icon {key.Icon}", e);
        }
        finally
        {
            if (request == _iconRequest) _iconLoading = false;
        }
    }

    public StatusTone Tone => _view.Tone;
    public string Headline => _view.Headline;
    public string Detail => _view.Detail;
    public string MainSetupInfo => _mainSetupInfo;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasAccount => _host.Engine.Status.Account is not null;
    public bool HasMainSetup { get; private set; }

    /// <summary>Respect the Windows "show animations" setting.</summary>
    public bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!Set(ref _startWithWindows, value)) return;
            _settings.StartWithWindows = value;
            _store.SaveSettings(_settings);
            if (!AppDataStore.IsSandboxed && Environment.ProcessPath is { } exe) StartupRegistration.Apply(value, exe);
        }
    }

    public void Refresh()
    {
        var status = _host.Engine.Status;
        var main = _store.LoadMainSetup();
        HasMainSetup = main is not null;
        _view = StatusText.Describe(status, HasMainSetup, DateTimeOffset.Now);
        _mainSetupInfo = main is null
            ? "No main setup yet"
            : $"Main setup from {main.CapturedFromRiotId}, updated {StatusText.Ago(DateTimeOffset.Now - main.CapturedAt)}";

        Warnings.Clear();
        foreach (var w in status.Warnings) Warnings.Add(w);

        Raise(nameof(AccountName));
        Raise(nameof(AccountDetail));
        Raise(nameof(AccountInitial));
        RefreshAccountIcon();
        Raise(nameof(Tone));
        Raise(nameof(Headline));
        Raise(nameof(Detail));
        Raise(nameof(MainSetupInfo));
        Raise(nameof(HasWarnings));
        Raise(nameof(HasAccount));
        Raise(nameof(HasMainSetup));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ReloadSettings()
    {
        _settings = _store.LoadSettings();
        foreach (var t in AccountSections.Concat(ExtraSections)) t.IsEnabled = _settings.IsEnabled(t.Section);
        Refresh();
    }

    private SectionToggleViewModel Toggle(SyncSection section, string title, string description) =>
        new(section, title, description, _settings.IsEnabled(section), OnSectionChanged);

    private void OnSectionChanged(SyncSection section, bool enabled)
    {
        _settings = _store.LoadSettings();
        if (enabled) _settings.EnabledSections.Add(section);
        else _settings.EnabledSections.Remove(section);
        _store.SaveSettings(_settings);
    }
}

/// <summary>What the view model needs from the app: dialogs and windows.</summary>
public interface IAppShell
{
    bool Confirm(string title, string message, string primary, string secondary);
    void ShowError(Exception e);
    void ShowEditor();
    void ShowBackups();
    void OpenFolder(string path);
    void ShowMessage(string title, string message);
    void Notify(string message);

    /// <summary>Quit right away so the update can replace the program.</summary>
    void ExitForUpdate();
}
