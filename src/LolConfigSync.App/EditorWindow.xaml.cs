using LolConfigSync.App.Controls;
using LolConfigSync.App.ViewModels;

namespace LolConfigSync.App;

public partial class EditorWindow : HexWindow
{
    public EditorWindow(Func<Action, EditorViewModel> createViewModel)
    {
        InitializeComponent();
        DataContext = createViewModel(Close);
    }
}
