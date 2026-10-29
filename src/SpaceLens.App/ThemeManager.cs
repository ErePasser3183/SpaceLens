using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpaceLens.App;

public enum AppThemeMode { System, Light, Dark }

public static class ThemeManager
{
    public static AppThemeMode Mode { get; private set; } = AppThemeMode.System;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;
    private static bool initialized;
    private static bool applied;
    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Application.Current.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        Apply();
    }
    private static void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Mode == AppThemeMode.System) Application.Current.Dispatcher.BeginInvoke(Apply);
    }
    public static void SetMode(AppThemeMode mode) { Mode = mode; Apply(); }
    public static bool ReadSystemDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }
    public static void Apply()
    {
        bool nextDark = Mode == AppThemeMode.Dark || Mode == AppThemeMode.System && ReadSystemDark();
        if (applied && nextDark == IsDark) return;
        IsDark = nextDark; applied = true;
        string[] keys = ["WindowBg", "Surface", "SurfaceAlt", "Ink", "Muted", "Line", "Hover", "Selected", "Accent", "AccentText", "Track"];
        string[] light = ["#F3F5F7", "#FFFFFF", "#F7F8FA", "#29333F", "#7A8695", "#E0E5EB", "#F0F4F7", "#E3EEF7", "#427DA6", "#FFFFFF", "#E5EBF0"];
        string[] dark = ["#17181D", "#23252B", "#292B32", "#E4E6EB", "#9C9FA9", "#383B44", "#30343D", "#30465C", "#62A5DC", "#142334", "#35404F"];
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString((IsDark ? dark : light)[i]));
            brush.Freeze(); Application.Current.Resources[keys[i]] = brush;
        }
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
        Changed?.Invoke();
    }
    public static void Attach(Window window)
    {
        void Setup()
        {
            ApplyTitleBar(window);
            if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is { } source)
                source.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
                {
                    if (msg == 0x001A && Mode == AppThemeMode.System) window.Dispatcher.BeginInvoke(Apply);
                    return 0;
                });
        }
        if (new WindowInteropHelper(window).Handle != 0) Setup();
        else window.SourceInitialized += (_, _) => Setup();
    }
    private static void ApplyTitleBar(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        int dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
