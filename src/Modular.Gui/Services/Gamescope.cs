using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Modular.Gui.Services;

/// <summary>
/// Adapts the GUI to gamescope (Steam Game Mode, or a nested gamescope).
/// </summary>
/// <remarks>
/// Gamescope shows one window at a time and stretches it to fill the display. It ignores
/// maximize requests and never resizes a window itself, so a window smaller than the
/// display is upscaled and blurred, and a popup that reaches past the window's edge makes
/// it rescale the whole view. Every window therefore has to be exactly display-sized, with
/// the UI scale carried by Avalonia rather than by gamescope's upscaler.
/// </remarks>
public static class Gamescope
{
    private const string ScaleVariable = "AVALONIA_GLOBAL_SCALE_FACTOR";
    private const string PerScreenScaleVariable = "AVALONIA_SCREEN_SCALE_FACTORS";

    // The layout is designed around this logical size; it is also the Steam Deck's panel.
    private const double DesignWidth = 1280;
    private const double DesignHeight = 800;

    /// <summary>True when the process runs inside a gamescope session.</summary>
    public static bool IsActive { get; } =
        OperatingSystem.IsLinux() &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY"));

    /// <summary>
    /// Picks a UI scale for the gamescope display and makes owned windows cover it. Call
    /// before Avalonia starts: the X11 backend reads the scale once, from the environment.
    /// A scale the user set is kept.
    /// </summary>
    public static void Initialize()
    {
        if (!IsActive)
            return;

        // Windows the app does not create itself, such as Avalonia's managed file picker.
        // The owner is assigned just before such a window is shown.
        WindowBase.OwnerProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            if (e.NewValue != null)
                Fit(window);
        });

        if (Environment.GetEnvironmentVariable(ScaleVariable) != null ||
            Environment.GetEnvironmentVariable(PerScreenScaleVariable) != null)
            return;

        if (TryGetDisplaySize(out var width, out var height))
        {
            Environment.SetEnvironmentVariable(
                ScaleVariable,
                ScaleFor(width, height).ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The largest quarter-step scale at which the design size still fits the display.
    /// </summary>
    public static double ScaleFor(int pixelWidth, int pixelHeight)
    {
        var fit = Math.Min(pixelWidth / DesignWidth, pixelHeight / DesignHeight);
        return Math.Max(1.0, Math.Floor(fit * 4) / 4);
    }

    /// <summary>
    /// Makes a window cover the display exactly, and keeps it that way when the display
    /// changes (docking, an external screen). Does nothing outside gamescope.
    /// </summary>
    public static void FillDisplay(Window window)
    {
        if (!IsActive)
            return;

        Fit(window);
        window.Screens.Changed += (_, _) => Fit(window);
    }

    private static void Fit(Window window)
    {
        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen == null)
            return;

        window.WindowState = WindowState.Normal;
        window.Position = screen.Bounds.Position;
        window.Width = screen.Bounds.Width / screen.Scaling;
        window.Height = screen.Bounds.Height / screen.Scaling;
    }

    private static bool TryGetDisplaySize(out int width, out int height)
    {
        width = height = 0;
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                return false;
            try
            {
                var screen = XDefaultScreen(display);
                width = XDisplayWidth(display, screen);
                height = XDisplayHeight(display, screen);
                return width > 0 && height > 0;
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayWidth(IntPtr display, int screen);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayHeight(IntPtr display, int screen);
}
