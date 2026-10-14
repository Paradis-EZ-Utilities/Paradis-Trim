using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ParadisTrim.Core;
using ParadisTrim.Native;

namespace ParadisTrim;

/// <summary>
/// Paradis Trim's own small message window, used instead of the standard Windows message box so
/// messages match the dark Paradis EZ look and buttons can say exactly what they do
/// ("Choose Folder…" instead of "Yes").
/// </summary>
public sealed class MessageDialog : Window
{
    private int _choice = -1;

    private MessageDialog(string message, string? detail, IReadOnlyList<string> buttons, int defaultButton, int cancelButton)
    {
        Title = AppInfo.ProductName;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        if (!string.IsNullOrEmpty(detail))
        {
            var detailText = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) };
            detailText.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            panel.Children.Add(detailText);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i],
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == defaultButton,
                IsCancel = i == cancelButton,
                MinWidth = 88,
            };
            button.SetResourceReference(StyleProperty, i == defaultButton ? "PrimaryButton" : "SecondaryButton");
            if (i == defaultButton) { button.FontSize = 13; button.Padding = new Thickness(14, 7, 14, 7); }
            button.Click += (_, _) => { _choice = index; Close(); };
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        Content = panel;

        DarkTitleBar.Apply(this);
        Loaded += (_, _) =>
        {
            if (row.Children.Count > defaultButton && defaultButton >= 0) ((Button)row.Children[defaultButton]).Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && cancelButton < 0) { _choice = -1; Close(); }
        };
    }

    /// <summary>Shows a message with the given buttons. Returns the index of the button clicked, or the cancel index if closed.</summary>
    public static int Show(Window? owner, string message, string? detail, IReadOnlyList<string> buttons, int defaultButton = 0, int cancelButton = -1)
    {
        var dialog = new MessageDialog(message, detail, buttons, defaultButton, cancelButton);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._choice < 0 ? cancelButton : dialog._choice;
    }

    /// <summary>A simple message with one OK button.</summary>
    public static void Inform(Window? owner, string message, string? detail = null) =>
        Show(owner, message, detail, ["OK"], 0, 0);
}
