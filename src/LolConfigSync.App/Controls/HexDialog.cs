using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LolConfigSync.App.Controls;

/// <summary>Themed replacement for MessageBox: a title, a message, and one or two buttons.</summary>
public sealed class HexDialog : HexWindow
{
    private HexDialog(string title, string message, string primary, string? secondary)
    {
        Title = "LoL Config Sync";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var heading = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 10) };
        heading.SetResourceReference(StyleProperty, "TitleText");

        var body = new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 24), LineHeight = 21 };
        body.SetResourceReference(StyleProperty, "BodyText");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (secondary is not null)
        {
            var cancel = new Button { Content = secondary, IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
            cancel.SetResourceReference(StyleProperty, "HexButton");
            cancel.Click += (_, _) => DialogResult = false;
            buttons.Children.Add(cancel);
        }
        var ok = new Button { Content = primary, IsDefault = true, IsCancel = secondary is null };
        ok.SetResourceReference(StyleProperty, "HexPrimaryButton");
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);

        Content = new StackPanel { Margin = new Thickness(28, 18, 28, 24), Children = { heading, body, buttons } };
        Loaded += (_, _) => ok.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) DialogResult = false;
        };
    }

    /// <summary>Shows the dialog; returns true when the primary button was chosen.</summary>
    public static bool Show(Window? owner, string title, string message, string primary = "OK", string? secondary = null)
    {
        var dialog = new HexDialog(title, message, primary, secondary);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }
        return dialog.ShowDialog() == true;
    }
}
