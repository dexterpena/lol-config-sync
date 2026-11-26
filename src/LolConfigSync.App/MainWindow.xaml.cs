using System.ComponentModel;
using System.Windows.Media.Animation;
using LolConfigSync.App.Controls;
using LolConfigSync.App.ViewModels;
using LolConfigSync.Core.Presentation;

namespace LolConfigSync.App;

public partial class MainWindow : HexWindow
{
    private readonly MainViewModel _vm;
    private Storyboard? _pulse;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Tone)) UpdatePulse();
        };
        Loaded += (_, _) => UpdatePulse();
    }

    /// <summary>When true, closing hides to the tray instead of exiting.</summary>
    public bool HideOnClose { get; set; } = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (HideOnClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void UpdatePulse()
    {
        _pulse ??= (Storyboard)FindResource("PulseStoryboard");
        if (_vm.Tone == StatusTone.Syncing && _vm.AnimationsEnabled) _pulse.Begin(this, isControllable: true);
        else _pulse.Stop(this);
    }
}
