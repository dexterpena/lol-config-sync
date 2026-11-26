using LolConfigSync.App.Controls;
using LolConfigSync.App.ViewModels;

namespace LolConfigSync.App;

public partial class BackupsWindow : HexWindow
{
    public BackupsWindow(Func<Action, BackupsViewModel> createViewModel)
    {
        InitializeComponent();
        DataContext = createViewModel(Close);
    }
}
