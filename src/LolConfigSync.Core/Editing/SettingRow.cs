using System.ComponentModel;
using System.Runtime.CompilerServices;
using LolConfigSync.Core.Model;

namespace LolConfigSync.Core.Editing;

/// <summary>
/// One editable setting in the editor grid. In account mode (<see cref="AccountMode"/>) the row edits one
/// account's own value: editing makes it the account's own, and turning <see cref="IsOwnValue"/> off returns
/// to <see cref="MainValue"/>.
/// </summary>
public sealed class SettingRow(SettingKey key, SyncSection section, string label, string value, bool sync, bool isBoolean = false,
    bool accountMode = false, string? mainValue = null, bool isOwnValue = false) : INotifyPropertyChanged
{
    private string _value = value;
    private bool _sync = sync;
    private bool _isOwnValue = isOwnValue;
    private bool _revertingToMain;

    public SettingKey Key { get; } = key;

    /// <summary>The value is an on/off switch (edited as "true"/"false").</summary>
    public bool IsBoolean { get; } = isBoolean;
    public SyncSection Section { get; } = section;
    public string Label { get; } = label;
    public string OriginalValue { get; } = value;

    /// <summary>True when the row edits one account's own value rather than the main setup.</summary>
    public bool AccountMode { get; } = accountMode;

    /// <summary>The main setup's value (account mode).</summary>
    public string MainValue { get; } = mainValue ?? value;

    /// <summary>Raw key text (e.g. <c>HUD.GlobalScale</c>) for search and tooltips.</summary>
    public string KeyText => $"{Key.Section}.{Key.Name}";

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModified)));
            if (AccountMode && !_revertingToMain && value != MainValue) IsOwnValue = true;
        }
    }

    public bool Sync
    {
        get => _sync;
        set => Set(ref _sync, value);
    }

    /// <summary>Account mode: this account uses its own value instead of the main setup's.</summary>
    public bool IsOwnValue
    {
        get => _isOwnValue;
        set
        {
            if (!Set(ref _isOwnValue, value) || value) return;
            _revertingToMain = true;
            Value = MainValue;
            _revertingToMain = false;
        }
    }

    public bool IsModified => _value != OriginalValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
