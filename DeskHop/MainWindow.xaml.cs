using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace DeskHop;

public partial class MainWindow : Window
{
    public string AppVersion { get; set; }

    private readonly ObservableCollection<WindowItem> _windowItems = [];
    private readonly ObservableCollection<MonitorItem> _monitorItems = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        AppVersion = GetApplicationVersion();

        WindowSelector.ItemsSource = _windowItems;
        MonitorSelector.ItemsSource = _monitorItems;

        RefreshData();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshData();
        StatusBarText.Text = "Lists refreshed.";
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowSelector.SelectedItem is not WindowItem selectedWindow)
        {
            StatusBarText.Text = "Select an app window first.";
            return;
        }

        if (MonitorSelector.SelectedItem is not MonitorItem selectedMonitor)
        {
            StatusBarText.Text = "Select a target monitor.";
            return;
        }

        if (!TryMoveWindowToMonitor(selectedWindow.Handle, selectedMonitor))
        {
            StatusBarText.Text = "Could not move the selected window.";
            return;
        }

        RefreshData(selectedWindow.ProcessId, selectedMonitor.Handle);
        StatusBarText.Text = $"Moved '{selectedWindow.Title}' to {selectedMonitor.DisplayName}.";
    }

    private void Selector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMoveButtonState();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            RefreshData();
            StatusBarText.Text = "Lists refreshed.";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && MoveButton.IsEnabled)
        {
            MoveButton_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void RefreshData(int? preferredProcessId = null, IntPtr? preferredMonitorHandle = null)
    {
        var selectedWindowProcessId = preferredProcessId
            ?? (WindowSelector.SelectedItem as WindowItem)?.ProcessId;
        var selectedMonitorHandle = preferredMonitorHandle
            ?? (MonitorSelector.SelectedItem as MonitorItem)?.Handle;

        RefreshMonitors();
        RefreshWindows();

        if (selectedWindowProcessId is int processId)
        {
            WindowSelector.SelectedItem = _windowItems.FirstOrDefault(w => w.ProcessId == processId);
        }

        if (selectedMonitorHandle is IntPtr monitorHandle)
        {
            MonitorSelector.SelectedItem = _monitorItems.FirstOrDefault(m => m.Handle == monitorHandle);
        }

        if (_monitorItems.Count > 0 && MonitorSelector.SelectedItem is null)
        {
            MonitorSelector.SelectedIndex = 0;
        }

        if (_windowItems.Count > 0 && WindowSelector.SelectedItem is null)
        {
            WindowSelector.SelectedIndex = 0;
        }

        UpdateMoveButtonState();

        if (_windowItems.Count == 0)
        {
            StatusBarText.Text = "No movable app windows found. Open an app and refresh.";
        }
    }

    private void UpdateMoveButtonState()
    {
        MoveButton.IsEnabled = WindowSelector.SelectedItem is WindowItem && MonitorSelector.SelectedItem is MonitorItem;
    }

    private void RefreshWindows()
    {
        _windowItems.Clear();

        var ownHandle = new WindowInteropHelper(this).Handle;

        var windows = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .Where(p => p.MainWindowHandle != ownHandle)
            .Where(p => IsTaskbarLikeWindow(p.MainWindowHandle))
            .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle))
            .Select(p => new WindowItem(p.Id, p.MainWindowHandle, p.MainWindowTitle, p.ProcessName))
            .OrderBy(w => w.Title)
            .ToList();

        foreach (var item in windows)
        {
            _windowItems.Add(item);
        }
    }

    private static bool IsTaskbarLikeWindow(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd))
        {
            return false;
        }

        if (GetAncestor(hWnd, GA_ROOTOWNER) != hWnd)
        {
            return false;
        }

        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        var exStyle = GetWindowExStyle(hWnd);
        var isToolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;
        if (isToolWindow)
        {
            return false;
        }

        if (IsWindowCloaked(hWnd))
        {
            return false;
        }

        if (!GetWindowRect(hWnd, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        return width > 0 && height > 0;
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0)
        {
            return false;
        }

        return cloaked != 0;
    }

    private static nint GetWindowExStyle(IntPtr hWnd)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr(hWnd, GWL_EXSTYLE)
            : GetWindowLong(hWnd, GWL_EXSTYLE);
    }

    private void RefreshMonitors()
    {
        _monitorItems.Clear();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                _monitorItems.Add(new MonitorItem(
                    monitor,
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Right,
                    info.rcMonitor.Bottom,
                    info.rcWork.Left,
                    info.rcWork.Top,
                    info.rcWork.Right,
                    info.rcWork.Bottom,
                    _monitorItems.Count + 1));
            }

            return true;
        }, IntPtr.Zero);
    }

    private static bool TryMoveWindowToMonitor(IntPtr windowHandle, MonitorItem targetMonitor)
    {
        if (!GetWindowRect(windowHandle, out var windowRect))
        {
            return false;
        }

        var currentMonitorHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        var currentInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(currentMonitorHandle, ref currentInfo))
        {
            return false;
        }

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;

        var relativeLeft = windowRect.Left - currentInfo.rcWork.Left;
        var relativeTop = windowRect.Top - currentInfo.rcWork.Top;

        var targetLeft = targetMonitor.WorkLeft + relativeLeft;
        var targetTop = targetMonitor.WorkTop + relativeTop;

        var maxLeft = targetMonitor.WorkRight - width;
        var maxTop = targetMonitor.WorkBottom - height;

        targetLeft = maxLeft < targetMonitor.WorkLeft
            ? targetMonitor.WorkLeft
            : Math.Clamp(targetLeft, targetMonitor.WorkLeft, maxLeft);

        targetTop = maxTop < targetMonitor.WorkTop
            ? targetMonitor.WorkTop
            : Math.Clamp(targetTop, targetMonitor.WorkTop, maxTop);

        return SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            targetLeft,
            targetTop,
            width,
            height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static string GetApplicationVersion()
    {
        // 1. Fallback for MSIX / Windows Store Packages (Modern .NET & .NET Framework)
        try
        {
            Type? packageType = Type.GetType("Windows.ApplicationModel.Package, Windows, ContentType=WindowsRuntime");
            if (packageType != null)
            {
                dynamic? currentPackage = packageType.GetProperty("Current")?.GetValue(null);
                if (currentPackage != null)
                {
                    var v = currentPackage.Id.Version;
                    return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                }
            }
        }
        catch { /* Proceed to next fallback */ }

        // 2. Fallback for Legacy ClickOnce Deployments (.NET Framework)
        try
        {
            Type? deploymentType = Type.GetType("System.Deployment.Application.ApplicationDeployment, System.Deployment, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
            if (deploymentType != null)
            {
                object? isNetworkDeployedObj = deploymentType.GetProperty("IsNetworkDeployed")?.GetValue(null);
                if (isNetworkDeployedObj is bool isNetworkDeployed && isNetworkDeployed)
                {
                    dynamic? currentDeployment = deploymentType.GetProperty("CurrentDeployment")?.GetValue(null);
                    Version? v = currentDeployment?.CurrentVersion;
                    if (v != null) return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                }
            }
        }
        catch { /* Proceed to next fallback */ }

        // 3. Fallback for AppDomain Activation Context (Reflection-safe)
        try
        {
            object? activationCtx = AppDomain.CurrentDomain.GetType().GetProperty("ActivationContext")?.GetValue(AppDomain.CurrentDomain);
            if (activationCtx != null)
            {
                object? identity = activationCtx.GetType().GetProperty("Identity")?.GetValue(activationCtx);
                string? versionStr = identity?.GetType().GetProperty("Version")?.GetValue(identity) as string;

                if (!string.IsNullOrEmpty(versionStr) && Version.TryParse(versionStr, out Version? v) && v != null)
                {
                    return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                }
            }
        }
        catch { /* Proceed to next fallback */ }

        // 4. Fallback to Entry Assembly Version (The standard .exe project version)
        try
        {
            Version? v = Assembly.GetEntryAssembly()?.GetName()?.Version;
            if (v != null) return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch { /* Proceed to next fallback */ }

        // 5. Fallback to Executing Assembly Version (The assembly containing this code block)
        try
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) return $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch { /* Proceed to next fallback */ }

        // 6. Ultimate Hardcoded Fallback
        return "Version 0.0.0.0";
    }

}
