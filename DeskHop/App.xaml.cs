using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;

namespace DeskHop;
/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly HttpClient UpdateHttpClient = new();

    private const string GitHubOwner = "jon-kim";
    private const string GitHubRepository = "DeskHop";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (!IsInVisualStudioContext())
        {
            await TryCheckForUpdatesAsync(mainWindow);
        }
    }

    public static bool IsInVisualStudioContext()
    {
        // Check if the code is rendering inside the designer preview
        if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
        {
            return true;
        }

        // Check if the code is running via F5 Debugging
        if (Debugger.IsAttached)
        {
            return true;
        }

        return false;
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
                $"A new version ({update.TagName}) is available. Install now? The app will restart automatically.",
                "Update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (prompt != MessageBoxResult.Yes)
            {
                return;
            }

            var started = await TryStartSelfUpdateAsync(update);
            if (!started)
            {
                MessageBox.Show(
                    owner,
                    "Update download/install could not be started. You can update manually from the Releases page.",
                    "Update failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Process.Start(new ProcessStartInfo
                {
                    FileName = update.ReleaseUrl,
                    UseShellExecute = true
                });
                return;
            }

            Current.Shutdown();
        }
        catch
        {
            // Do not block app startup if update check fails.
        }
    }

    private static async Task<bool> TryStartSelfUpdateAsync(UpdateInfo update)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exeName = Path.GetFileName(exePath);

            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                "DeskHopUpdater",
                update.TagName,
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempRoot);

            var zipPath = Path.Combine(tempRoot, update.AssetName);
            await using (var stream = await UpdateHttpClient.GetStreamAsync(update.AssetDownloadUrl))
            await using (var file = File.Create(zipPath))
            {
                await stream.CopyToAsync(file);
            }

            var extractPath = Path.Combine(tempRoot, "extracted");
            var scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
            var script = BuildUpdaterScript(zipPath, extractPath, appDirectory, exeName);
            await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildUpdaterScript(string zipPath, string extractPath, string targetPath, string exeName)
    {
        var escapedZip = EscapeForPowerShellLiteral(zipPath);
        var escapedExtract = EscapeForPowerShellLiteral(extractPath);
        var escapedTarget = EscapeForPowerShellLiteral(targetPath);
        var escapedExe = EscapeForPowerShellLiteral(exeName);

        return $@"$ErrorActionPreference = 'Stop'
$zipPath = '{escapedZip}'
$extractPath = '{escapedExtract}'
$targetPath = '{escapedTarget}'
$exeName = '{escapedExe}'

Start-Sleep -Seconds 2
if (Test-Path $extractPath) {{
    Remove-Item $extractPath -Recurse -Force
}}

Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

$copied = $false
for ($i = 0; $i -lt 30; $i++) {{
    try {{
        Copy-Item -Path (Join-Path $extractPath '*') -Destination $targetPath -Recurse -Force
        $copied = $true
        break
    }}
    catch {{
        Start-Sleep -Seconds 1
    }}
}}

if (-not $copied) {{
    throw 'Could not copy update files.'
}}

Start-Process -FilePath (Join-Path $targetPath $exeName)
";
    }

    private static string EscapeForPowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}

