using System.Windows;
using System.Windows.Controls;
using ParadisTrim.Core.Infrastructure;
using ParadisTrim.Core.Trimming;
using ParadisTrim.Native;

namespace ParadisTrim;

/// <summary>
/// The small Settings window: the file name ending, and whether to open the folder after saving.
/// Changes are only kept when the user clicks Save.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _settings = settings;
        SuffixBox.Text = settings.FileSuffix;
        OpenFolderToggle.IsChecked = settings.OpenFolderAfterSave;
        Loaded += (_, _) => { SuffixBox.Focus(); SuffixBox.SelectAll(); };
    }

    private void OnSuffixChanged(object sender, TextChangedEventArgs e)
    {
        var error = OutputNaming.ValidateSuffix(SuffixBox.Text);
        SuffixErrorText.Text = error ?? "";
        SuffixErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = error is null;

        var suffix = OutputNaming.NormalizeSuffix(SuffixBox.Text);
        var blank = string.IsNullOrWhiteSpace(SuffixBox.Text);
        SuffixExampleText.Text = error is not null ? "" :
            $"Example: Song.mp3 → Song{suffix}.mp3" + (blank ? "  (blank uses the standard ending)" : "");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (OutputNaming.ValidateSuffix(SuffixBox.Text) is not null) return;
        _settings.FileSuffix = OutputNaming.NormalizeSuffix(SuffixBox.Text);
        _settings.OpenFolderAfterSave = OpenFolderToggle.IsChecked == true;
        _settings.Save();
        Log.Info($"Settings changed: suffix '{_settings.FileSuffix}', open folder after saving {_settings.OpenFolderAfterSave}");
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
