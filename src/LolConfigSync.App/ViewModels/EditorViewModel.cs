using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using LolConfigSync.Core.Editing;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.App.ViewModels;

public sealed record EditorTab(string Title, SyncSection? Section);

public sealed class EditorViewModel : ObservableObject
{
    private readonly SetupSnapshot _setup;
    private readonly AppSettings _settings;
    private readonly SyncEngine _engine;
    private readonly IReadOnlyList<SettingRow> _rows;
    private EditorTab _selectedTab;
    private string _search = "";
    private string _error = "";

    public EditorViewModel(SetupSnapshot mainSetup, AppSettings settings, SyncEngine engine, Action close, Action<Exception> onError)
    {
        _setup = mainSetup.DeepClone();
        _settings = settings;
        _engine = engine;
        _rows = SettingRows.Build(_setup, _settings);
        foreach (var row in _rows) row.PropertyChanged += OnRowChanged;

        Tabs =
        [
            new("Hotkeys", SyncSection.Hotkeys),
            new("Interface", SyncSection.Interface),
            new("Game", SyncSection.Game),
            new("Sound", SyncSection.Sound),
            new("Graphics", SyncSection.Video),
            new("All settings", null),
        ];
        _selectedTab = Tabs[0];

        Rows = CollectionViewSource.GetDefaultView(_rows);
        Rows.Filter = o => o is SettingRow row && Matches(row);

        SaveCommand = new RelayCommand(async () =>
        {
            var rejected = SettingRows.ApplyTo(_setup, _settings, _rows);
            if (rejected.Count > 0)
            {
                var names = string.Join(", ", rejected.Select(k => _rows.First(r => r.Key == k).Label));
                Error = $"These values need to be a number or true/false: {names}. Fix them and save again.";
                return;
            }
            await _engine.SaveEditedSetupAsync(_setup, _settings, CancellationToken.None);
            close();
        }, onError: onError);
        CancelCommand = new RelayCommand(close);
    }

    public IReadOnlyList<EditorTab> Tabs { get; }
    public ICollectionView Rows { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public EditorTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            Rows.Refresh();
            Raise(nameof(EmptyMessage));
            Raise(nameof(IsEmpty));
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            Rows.Refresh();
            Raise(nameof(EmptyMessage));
            Raise(nameof(IsEmpty));
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            Set(ref _error, value);
            Raise(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;
    public bool IsEmpty => Rows.IsEmpty;

    public string EmptyMessage => _search.Length > 0
        ? $"No settings match \"{_search}\"."
        : "Nothing here yet. Settings show up after they've been captured from an account.";

    public string ChangeSummary
    {
        get
        {
            var edits = _rows.Count(r => r.IsModified);
            var excluded = _rows.Count(r => !r.Sync);
            var parts = new List<string>();
            if (edits > 0) parts.Add(edits == 1 ? "1 change" : $"{edits} changes");
            if (excluded > 0) parts.Add($"{excluded} kept per account");
            return parts.Count == 0 ? "No changes" : string.Join(", ", parts);
        }
    }

    private bool Matches(SettingRow row)
    {
        if (_selectedTab.Section is { } section && row.Section != section) return false;
        if (_search.Length == 0) return true;
        return row.Label.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
            || row.KeyText.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || row.Value.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(ChangeSummary));
        if (HasError) Error = "";
    }
}
