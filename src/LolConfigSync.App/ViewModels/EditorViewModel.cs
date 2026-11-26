using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using LolConfigSync.Core.Editing;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.App.ViewModels;

public sealed record EditorTab(string Title, SyncSection? Section);

/// <summary>Who the editor is editing: the main setup (Puuid null) or one account's own values.</summary>
public sealed record EditTarget(string Label, string? Puuid, string RiotId)
{
    /// <summary>Screen readers and the dropdown's accessible name use this.</summary>
    public override string ToString() => Label;
}

public sealed class EditorViewModel : ObservableObject
{
    private readonly SetupSnapshot _setup;
    private readonly AppSettings _settings;
    private readonly AccountOverrides _overrides;
    private readonly SyncEngine _engine;
    private readonly Dictionary<string, IReadOnlyList<SettingRow>> _rowsByTarget = [];
    private IReadOnlyList<SettingRow> _rows = [];
    private EditTarget _target;
    private EditorTab _selectedTab;
    private string _search = "";
    private string _error = "";
    private ICollectionView _view = null!;

    public EditorViewModel(SetupSnapshot mainSetup, AppSettings settings, AccountOverrides overrides,
        IReadOnlyList<(string Puuid, string RiotId)> accounts, string? currentPuuid, SyncEngine engine,
        Action close, Action<Exception> onError)
    {
        _setup = mainSetup.DeepClone();
        _settings = settings;
        _overrides = overrides.DeepClone();
        _engine = engine;

        Targets =
        [
            new EditTarget("All accounts (main setup)", null, ""),
            .. accounts.OrderByDescending(a => a.Puuid == currentPuuid).ThenBy(a => a.RiotId, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new EditTarget(a.Puuid == currentPuuid ? $"{a.RiotId} (logged in)" : a.RiotId, a.Puuid, a.RiotId)),
        ];
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
        _target = Targets[0];
        LoadRows();

        SaveCommand = new RelayCommand(async () =>
        {
            var rejected = new List<SettingRow>();
            foreach (var (key, rows) in _rowsByTarget)
            {
                var target = Targets.First(t => (t.Puuid ?? "") == key);
                var bad = target.Puuid is null
                    ? SettingRows.ApplyTo(_setup, _settings, rows)
                    : SettingRows.ApplyToAccount(_overrides, target.Puuid, target.RiotId, _setup, rows);
                rejected.AddRange(rows.Where(r => bad.Contains(r.Key)));
            }
            if (rejected.Count > 0)
            {
                var names = string.Join(", ", rejected.Select(r => r.Label).Distinct());
                Error = $"These values need to be a number or true/false: {names}. Fix them and save again.";
                return;
            }
            await _engine.SaveEditedSetupAsync(_setup, _settings, CancellationToken.None, _overrides);
            close();
        }, onError: onError);
        CancelCommand = new RelayCommand(close);
    }

    public IReadOnlyList<EditTarget> Targets { get; }
    public IReadOnlyList<EditorTab> Tabs { get; }
    public ICollectionView Rows => _view;
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public EditTarget Target
    {
        get => _target;
        set
        {
            if (value is null || !Set(ref _target, value)) return;
            LoadRows();
            Raise(nameof(IsAccountMode));
            Raise(nameof(Subtitle));
        }
    }

    public bool IsAccountMode => _target.Puuid is not null;

    public string Subtitle => IsAccountMode
        ? $"Values you change here apply only to {_target.RiotId}, on top of your main setup. Untick \"Own value\" to go back to the main setup's value."
        : "Changes are saved to your main setup and applied to the account you're logged into. Uncheck Sync to let a setting stay different on each account.";

    public EditorTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            RefreshView();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            RefreshView();
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
    public bool IsEmpty => _view.IsEmpty;

    public string EmptyMessage => _search.Length > 0
        ? $"No settings match \"{_search}\"."
        : "Nothing here yet. Settings show up after they've been captured from an account.";

    public string ChangeSummary
    {
        get
        {
            var edits = _rowsByTarget.Values.SelectMany(r => r).Count(r => r.IsModified);
            var parts = new List<string>();
            if (edits > 0) parts.Add(edits == 1 ? "1 change" : $"{edits} changes");
            if (IsAccountMode)
            {
                var own = _rows.Count(r => r.IsOwnValue);
                parts.Add(own == 1 ? $"1 own value for {_target.RiotId}" : $"{own} own values for {_target.RiotId}");
            }
            else
            {
                var excluded = _rows.Count(r => !r.Sync);
                if (excluded > 0) parts.Add($"{excluded} kept per account");
            }
            return parts.Count == 0 ? "No changes" : string.Join(", ", parts);
        }
    }

    /// <summary>Rows are kept per target, so switching accounts doesn't lose unsaved edits.</summary>
    private void LoadRows()
    {
        var key = _target.Puuid ?? "";
        if (!_rowsByTarget.TryGetValue(key, out var rows))
        {
            rows = _target.Puuid is null
                ? SettingRows.Build(_setup, _settings)
                : SettingRows.BuildForAccount(_setup, _settings, _overrides, _target.Puuid);
            foreach (var row in rows) row.PropertyChanged += OnRowChanged;
            _rowsByTarget[key] = rows;
        }
        _rows = rows;
        _view = new ListCollectionView(rows.ToList()) { Filter = o => o is SettingRow row && Matches(row) };
        Raise(nameof(Rows));
        RefreshView();
    }

    private void RefreshView()
    {
        _view.Refresh();
        Raise(nameof(EmptyMessage));
        Raise(nameof(IsEmpty));
        Raise(nameof(ChangeSummary));
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
