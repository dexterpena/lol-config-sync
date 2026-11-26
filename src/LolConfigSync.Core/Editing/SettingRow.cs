using System.ComponentModel;
using System.Runtime.CompilerServices;
using LolConfigSync.Core.Model;

namespace LolConfigSync.Core.Editing;

/// <summary>One editable setting in the editor grid.</summary>
public sealed class SettingRow(SettingKey key, SyncSection section, string label, string value, bool sync, bool isBoolean = false)
    : INotifyPropertyChanged
{
    private string _value = value;
    private bool _sync = sync;

    public SettingKey Key { get; } = key;

    /// <summary>The value is an on/off switch (edited as "true"/"false").</summary>
    public bool IsBoolean { get; } = isBoolean;
    public SyncSection Section { get; } = section;
    public string Label { get; } = label;
    public string OriginalValue { get; } = value;

    /// <summary>Raw key text (e.g. <c>HUD.GlobalScale</c>) for search and tooltips.</summary>
    public string KeyText => $"{Key.Section}.{Key.Name}";

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModified)));
        }
    }

    public bool Sync
    {
        get => _sync;
        set => Set(ref _sync, value);
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
