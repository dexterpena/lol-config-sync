using System.Windows;
using LolConfigSync.App.Controls;
using LolConfigSync.App.ViewModels;

namespace LolConfigSync.App;

public partial class EditorWindow : HexWindow
{
    public EditorWindow(Func<Action, EditorViewModel> createViewModel)
    {
        InitializeComponent();
        var vm = createViewModel(Close);
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.IsAccountMode)) ShowColumnsFor(vm.IsAccountMode);
        };
        ShowColumnsFor(vm.IsAccountMode);
    }

    /// <summary>Main setup: Sync column. One account: Own value + Main setup columns.</summary>
    private void ShowColumnsFor(bool accountMode)
    {
        SyncColumn.Visibility = accountMode ? Visibility.Collapsed : Visibility.Visible;
        OwnColumn.Visibility = accountMode ? Visibility.Visible : Visibility.Collapsed;
        MainValueColumn.Visibility = accountMode ? Visibility.Visible : Visibility.Collapsed;
        if (accountMode && IsLoaded) SaveSnapshotIfRequested("account");
    }
}
