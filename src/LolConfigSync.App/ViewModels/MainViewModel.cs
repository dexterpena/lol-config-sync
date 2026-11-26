using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LolConfigSync.App.Platform;
using LolConfigSync.Core.Hosting;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Presentation;
using LolConfigSync.Core.Storage;
using LolConfigSync.Core.Sync;

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
}
