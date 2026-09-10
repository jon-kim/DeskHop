using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenManager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<WindowItem> _windowItems = [];
    private readonly ObservableCollection<MonitorItem> _monitorItems = [];

    public MainWindow()
    {
        InitializeComponent();

        WindowSelector.ItemsSource = _windowItems;
        MonitorSelector.ItemsSource = _monitorItems;

        RefreshData();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshData();
        StatusText.Text = "Lists refreshed.";
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowSelector.SelectedItem is not WindowItem selectedWindow)
        {
            StatusText.Text = "Select an app window first.";
            return;
        }

        if (MonitorSelector.SelectedItem is not MonitorItem selectedMonitor)
        {
            StatusText.Text = "Select a target monitor.";
            return;
        }

        if (!TryMoveWindowToMonitor(selectedWindow.Handle, selectedMonitor))
        {
            StatusText.Text = "Could not move the selected window.";
            return;
        }

        RefreshData(selectedWindow.ProcessId, selectedMonitor.Handle);
        StatusText.Text = $"Moved '{selectedWindow.Title}' to {selectedMonitor.DisplayName}.";
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
            StatusText.Text = "Lists refreshed.";
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
            StatusText.Text = "No movable app windows found. Open an app and refresh.";
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

    private sealed record WindowItem(int ProcessId, IntPtr Handle, string Title, string ProcessName)
    {
        public string DisplayName => $"{Title} — {ProcessName} (PID {ProcessId})";
    }

    private sealed record MonitorItem(
        IntPtr Handle,
        int Left,
        int Top,
        int Right,
        int Bottom,
        int WorkLeft,
        int WorkTop,
        int WorkRight,
        int WorkBottom,
        int Index)
    {
        public string DisplayName => $"Monitor {Index}: {WorkRight - WorkLeft} x {WorkBottom - WorkTop}";
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint GW_OWNER = 4;
    private const uint GA_ROOTOWNER = 3;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern nint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
