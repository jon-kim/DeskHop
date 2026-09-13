using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace DeskHop;
/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string GitHubOwner = "jon-kim";
    private const string GitHubRepository = "ScreenManager";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        await TryCheckForUpdatesAsync(mainWindow);
    }

    private static async Task TryCheckForUpdatesAsync(Window owner)
    {
        try
        {
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion is null)
            {
                return;
            }

            var checker = new UpdateChecker(GitHubOwner, GitHubRepository);
            var update = await checker.GetAvailableUpdateAsync(currentVersion);
            if (update is null)
            {
                return;
            }

            var prompt = MessageBox.Show(
                owner,
                $"A new version ({update.TagName}) is available. Open the GitHub release page now?",
                "Update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (prompt == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = update.ReleaseUrl,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Do not block app startup if update check fails.
        }
    }
}

