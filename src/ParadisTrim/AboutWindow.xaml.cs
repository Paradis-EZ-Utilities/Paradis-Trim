using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ParadisTrim.Core;
using ParadisTrim.Core.Infrastructure;
using ParadisTrim.Native;

namespace ParadisTrim;

/// <summary>A small, quiet About window. The web links come from AppInfo and are hidden when not set.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        TaglineText.Text = AppInfo.Tagline;
        VersionText.Text = $"{AppInfo.ProductName} · Version {AppInfo.Version}";
        DescriptionText.Text = AppInfo.Description;
        FamilyText.Text = AppInfo.FamilyName;

        SetUpLink(MoreLink, AppInfo.MoreUtilitiesUrl);
        SetUpLink(SourceLink, AppInfo.ViewSourceUrl);
        SetUpLink(SupportLink, AppInfo.SupportUrl);
    }

    private static void SetUpLink(Button button, string url)
    {
        button.Tag = url;
        button.ToolTip = url;
        button.Visibility = string.IsNullOrWhiteSpace(url) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && url.Length > 0 && !OpenWithShell(url))
        {
            // No default browser (or it refused): show the address so it can be typed in by hand.
            ShowProblem($"Your web browser couldn't be opened. The address is: {url}");
        }
    }

    private void OnNoticesClick(object sender, RoutedEventArgs e)
    {
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (!File.Exists(notices)) ShowProblem("The third-party notices file is missing from the Paradis Trim folder.");
        else if (!OpenWithShell(notices)) ShowProblem($"The notices couldn't be opened. They're in {notices}");
    }

    private void OnLogClick(object sender, RoutedEventArgs e)
    {
        if (!OpenWithShell(AppPaths.LogFolder)) ShowProblem($"The log folder couldn't be opened. It's {AppPaths.LogFolder}");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ShowProblem(string text)
    {
        LinkProblemText.Text = text;
        LinkProblemText.Visibility = Visibility.Visible;
    }

    private static bool OpenWithShell(string target)
    {
        try
        {
            Log.Info($"Opening {target}");
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
            return false;
        }
    }
}
