using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using LolConfigSync.Core.Presentation;
using LolConfigSync.Core.Storage;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.App.ViewModels;

public sealed class BackupItem(BackupEntry entry)
{
    public BackupEntry Entry { get; } = entry;
    public string RiotId => string.IsNullOrEmpty(Entry.RiotId) ? "Unknown account" : Entry.RiotId;
    public string When => Entry.IsOriginal
        ? $"Original settings ({Entry.CreatedAt.ToLocalTime():MMM d})"
        : Entry.CreatedAt.ToLocalTime().ToString("MMM d, h:mm tt");
    public string Ago => StatusText.Ago(DateTimeOffset.Now - Entry.CreatedAt);
}

public sealed class BackupsViewModel : ObservableObject
{
    private readonly SyncEngine _engine;
    private BackupItem? _selected;
    private string _message = "";

    public BackupsViewModel(AppDataStore store, SyncEngine engine, Func<string, string, bool> confirm, Action<Exception> onError, Action close)
    {
        _engine = engine;
        var items = store.ListBackups().Select(b => new BackupItem(b)).ToList();
        Items = CollectionViewSource.GetDefaultView(items);
        Items.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BackupItem.RiotId)));
        IsEmpty = items.Count == 0;

        RestoreCommand = new RelayCommand(async () =>
        {
            var item = _selected!;
            if (!confirm($"Restore {item.RiotId}?",
                    $"This puts back {item.RiotId}'s settings from {item.When}, replacing what the account has now. " +
                    "Your main setup stays the same, and syncing pauses for this account until you press Sync now."))
            {
                return;
            }
            var result = await _engine.RestoreBackupAsync(item.Entry.Path, CancellationToken.None);
            if (result.Warnings.Count > 0)
            {
                Message = string.Join(Environment.NewLine, result.Warnings);
                return;
            }
            Message = $"Restored {item.RiotId} to {item.When}. Syncing is paused for this account until you press Sync now.";
        }, () => CanRestore, onError);
        CloseCommand = new RelayCommand(close);
    }

    public ICollectionView Items { get; }
    public bool IsEmpty { get; }
    public ICommand RestoreCommand { get; }
    public ICommand CloseCommand { get; }

    public BackupItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Message = "";
            Raise(nameof(Hint));
        }
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value)) Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    private bool CanRestore => _selected is not null && _selected.Entry.Puuid == _engine.Status.Account?.Puuid;

    public string Hint => _selected switch
    {
        null => "Pick a backup to restore it.",
        _ when CanRestore => $"Restoring replaces {_selected.RiotId}'s current settings.",
        _ => $"Log into {_selected.RiotId} to restore this backup.",
    };
}
