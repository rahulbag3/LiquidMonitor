using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace LiquidMonitor;

public sealed partial class MainWindow : Window
{
    
    private DispatcherTimer _timer;
    private DispatcherTimer _pingTimer;

    private long _lastSent;
    private long _lastReceived;
    private ulong _lastCpuIdle;
    private ulong _lastCpuTotal;
    private bool _cpuInitialized;
    private IntPtr _gpuQuery;
    private IntPtr _gpuCounter;

    private DateTime _lastSample = DateTime.UtcNow;

    private bool _firstSample = true;

    private string? _adapterId;
    private bool _adapterChanged;

    private bool _expanded = false;
    private bool _alwaysOnTop = false;
    private bool _initialPositionSet = false;


    private nint _hwnd;

    // Pointer travel in physical pixels before a press becomes a drag.
    // Stops the pill from chasing the cursor on a click's tiny jitter.
    private const int DragThreshold = 4;

    private bool _pressing;
    private bool _dragging;

    // Full-screen BGRA grab, kept so that dragging can re-cut the glass
    // without touching the screen again. It does contain this window; the
    // window's own rectangle is rebuilt in the crop, not here.
    private byte[]? _screenCache;
    private int _cacheX;
    private int _cacheY;
    private int _cacheWidth;
    private int _cacheHeight;

    private int _lastDragRenderTick;
    private int _backdropGeneration;
    private bool _backdropRenderBusy;
    private DispatcherTimer? _backdropTimer;

    // Measured compact pill size, cached after the first fit so later
    // switches back to compact are immediate.
    private int _compactFitWidth;
    private int _compactFitHeight;

    // Opaque base the compact pill is painted with.
    private Color _compactBase =
        Color.FromArgb(0xFF, 0x10, 0x15, 0x1F);

    // Live values parked while the widest strings are shown for measuring.
    private string _compactProbeDown = string.Empty;
    private string _compactProbeUp = string.Empty;
    private bool _ignoreNextDrag;
    private ThemeMode _theme = ThemeMode.LiquidGlass;

    private enum ThemeMode
    {
        LiquidGlass,
        Material3,
        FrostedGlass
    }

    private POINT _mouseStart;

    private PointInt32 _windowStart;
    private Point _dragOffset;



    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFormattedValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedItem
    {
        public IntPtr Name;
        public PdhFormattedValue Value;
    }



    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhOpenQueryW(IntPtr dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
    nint hWnd,
    nint hWndInsertAfter,
    int X,
    int Y,
    int cx,
    int cy,
    uint flags);


    private const nint HWND_TOPMOST = -1;
    private const nint HWND_NOTOPMOST = -2;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    // DWMWINDOWCORNERPREFERENCE values. Note that 2 is ROUNDSMALL (4px),
    // not ROUND -- the old code passed 2 while calling it "ROUND", which is
    // why the window corners were barely rounded and black showed at the
    // card's much larger CornerRadius.
    // DWMWINDOWCORNERPREFERENCE. Value 2 is ROUNDSMALL (4px) -- the old code
    // called 2 "ROUND", which is why the corners barely rounded. Value 5
    // (DWMWCP_ROUNDED) returns E_INVALIDARG on this Windows build, so
    // ROUNDLARGE is the largest radius actually available here.
    private const int DWMWCP_NONE = 1;
    private const int DWMWCP_ROUND = 3;
    private const int DWMWCP_ROUNDLARGE = 4;
    private const int DWMWCP_ROUNDED = 5;

    // Windows 11 paints a subtle border around a window that has rounded
    // corners. With the compact pill shaped by a window region, that border
    // shows up as a light rounded-rectangle outline sitting outside the pill,
    // so it gets switched off explicitly.
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);


    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int value,
        int size);


    // Aero's blur-behind (DWMWA_ACCENT_POLICY) was tried here because it is
    // the mechanism Aero itself used. It had no measurable effect on this
    // window at any tint alpha, so it is not used. Transparency comes from
    // SystemBackdrop instead, which is the only path verified to sample the
    // desktop.

    private const int GWL_STYLE = -16;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(
        int left,
        int top,
        int right,
        int bottom);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    public MainWindow()
    {
        InitializeComponent();


        _hwnd =
            WindowNative.GetWindowHandle(this);

        ApplyWindowShape();

        SetupWindow();
        InitializeGpuCounter();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };


        _timer.Tick += Timer_Tick;

        _timer.Start();

        _pingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };

        _pingTimer.Tick += async (_, __) =>
        {
            await UpdateNetworkInfo();
        };

        _pingTimer.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _pingTimer.Stop();

            if (_backdropTimer is not null)
                _backdropTimer.Stop();

            if (_gpuQuery != IntPtr.Zero)
                PdhCloseQuery(_gpuQuery);
        };

        ApplyTheme(_theme);
        ShowCompact();

        PrimeNetworkBaseline();

        // The glass refreshes itself so it follows the desktop without the user
        // having to move the widget. The capture never clips or hides the
        // window, so no refresh can produce a blank frame.
        _backdropTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(BackdropRefreshMs)
        };

        _backdropTimer.Tick += (_, _) => RefreshBackdropFromScreen();

        _backdropTimer.Start();

        ScheduleBackdropRefresh();

    }

    /// <summary>
    /// The pale tint laid over the captured, blurred desktop.
    ///
    /// The see-through itself is done by RefreshBackdrop, which grabs the real
    /// screen and blurs it. DWM is not used at all: measured on this machine
    /// the acrylic backdrop served its opaque FallbackColor instead of the
    /// desktop, so it could never produce transparency. The full set of ruled
    /// out causes is recorded on RefreshBackdrop.
    ///
    /// This alpha is the dial between "clear glass" and "milky panel":
    ///   0x20 -> strongly see through
    ///   0x4D -> balanced
    ///   0x8C -> pale and nearly opaque
    /// </summary>
    private static Brush CreateGlassTintBrush()
    {
        // LM_GLASS_SCRIM takes a hex alpha (00..FF) so the transparency can be
        // tuned without a rebuild. Default is the balanced value.
        uint alpha = GlassScrimAlpha;

        if (uint.TryParse(
                Environment.GetEnvironmentVariable("LM_GLASS_SCRIM"),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out uint probe))
        {
            alpha = probe;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.2, 1)
        };

        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)alpha, 0xD8, 0xE4, 0xF6), Offset = 0.00 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)alpha, 0xC6, 0xD6, 0xEE), Offset = 0.50 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)alpha, 0xB0, 0xC2, 0xDE), Offset = 1.00 });

        return brush;
    }

    /// <summary>
    /// How much the pale scrim covers the captured desktop. Higher is paler and
    /// more opaque; lower lets more of the real desktop through.
    /// </summary>
    private const uint GlassScrimAlpha = 0x2E;

    /// <summary>
    /// Blur strength. Three box passes at radius 10 give a sigma around 11px,
    /// down from 17. The glass is meant to show the desktop through it, and at
    /// 17 it was close to frosted rather than transparent.
    /// </summary>
    private const int BlurRadius = 10;
    private const int BlurPasses = 3;

    /// <summary>
    /// Minimum gap between backdrop repaints during a drag. Each one blurs,
    /// encodes and decodes a small bitmap, so running it on every pointer move
    /// wastes work the eye cannot use. Around 25fps is enough for the glass to
    /// read as following the cursor.
    /// </summary>
    private const int DragRenderIntervalMs = 40;

    /// <summary>
    /// How often the glass refreshes from the screen on its own. Short, because
    /// the capture is the only real cost and it cannot blink.
    /// </summary>
    private const int BackdropRefreshMs = 150;

    /// <summary>
    /// How far past the window's own rectangle the reconstruction reaches, and
    /// how far outside that the genuine border is sampled from.
    ///
    /// The first value has to clear the drop shadow, otherwise the shadow is
    /// sampled as though it were desktop and dark corners appear on the glass.
    /// The second is extra margin on top of that, because a shadow is soft and
    /// has no hard edge to sample exactly. Both must stay well inside the 72px
    /// crop margin, which is what leaves a ring of real desktop on the glass
    /// between the card and the edge of the capture.
    /// </summary>
    private const int InpaintExpandPx = 16;
    private const int InpaintSkipPx = 16;

    /// <summary>
    /// Periodic refresh so the glass follows the desktop on its own, instead of
    /// only when the widget happens to be moved.
    ///
    /// Every tick takes the same path: grab the screen with the window left
    /// visible, then rebuild the window's rectangle from the genuine border
    /// around it. Nothing is ever hidden or clipped, so there is no blank
    /// frame left for anything to flicker on, and the area directly behind the
    /// widget is as fresh as the rest of the backdrop instead of being held
    /// back by a slower second capture.
    /// </summary>
    private void RefreshBackdropFromScreen()
    {
        if (_hwnd == 0 || _theme != ThemeMode.LiquidGlass)
            return;

        // While dragging, the only job is to re-cut the glass at the new
        // position from the existing cache. Capturing the screen as well would
        // contend with the render for the same work and is what made dragging
        // feel slow, especially on the much larger expanded card.
        //
        // Re-cutting is still correct, because the cache is a real capture and
        // the window's rectangle is rebuilt on every crop regardless of which
        // cache the crop came from.
        if (_dragging)
        {
            QueueBackdropRender();
            return;
        }

        CaptureScreen();

        QueueBackdropRender();
    }


    /// <summary>
    /// Grabs the whole screen with the window left visible and caches it.
    ///
    /// The grab necessarily contains this window, so the window's rectangle is
    /// rebuilt afterwards, in the crop, from the genuine border around it.
    /// Nothing is hidden, clipped or restyled here, so a capture cannot cost a
    /// blank frame.
    ///
    /// This replaces a two tier design: a fast grab every 150ms, plus a "clean"
    /// grab every 4s that clipped the window out of its own capture. The clean
    /// grab was the only thing in the app that could blink, and the fast grab
    /// pasted that four second old capture back over the window's rectangle, so
    /// the area directly behind the widget was frozen for up to four seconds.
    /// Both reported symptoms came from that split.
    /// </summary>
    private void CaptureScreen()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
            return;

        byte[]? pixels = ScreenCapture.Capture(x, y, width, height);

        if (pixels is null)
            return;

        _screenCache = pixels;
        _cacheX = x;
        _cacheY = y;
        _cacheWidth = width;
        _cacheHeight = height;
    }


    /// <summary>
    /// Blurs and encodes the glass region on a background thread, then swaps it
    /// in on the UI thread.
    ///
    /// The blur is the expensive part, and running it inline made dragging
    /// stutter because PointerMoved executes on the UI thread and every pass
    /// blocked the frame. A pass is also skipped entirely while a previous one
    /// is still in flight, so a slow pass costs frame rate instead of building
    /// a backlog of stale results.
    /// </summary>
    private void QueueBackdropRender()
    {
        if (_hwnd == 0 || _theme != ThemeMode.LiquidGlass)
            return;

        byte[]? cache = _screenCache;

        if (cache is null || _backdropRenderBusy)
            return;

        if (!GetWindowRect(_hwnd, out RECT rect))
            return;

        int windowWidth = rect.Right - rect.Left;
        int windowHeight = rect.Bottom - rect.Top;

        int cacheX = _cacheX;
        int cacheY = _cacheY;
        int cacheW = _cacheWidth;
        int cacheH = _cacheHeight;

        // Wider than the blur radius, or the corners fade to transparent.
        const int margin = 72;

        int width = (rect.Right - rect.Left) + (margin * 2);
        int height = (rect.Bottom - rect.Top) + (margin * 2);

        int sourceX = (rect.Left - margin) - cacheX;
        int sourceY = (rect.Top - margin) - cacheY;

        // No bounds check here on purpose.
        //
        // The rectangle includes a margin on every side, so it hangs off the
        // edge of the screen whenever the card comes within that margin of a
        // boundary. This used to return early in that case, which meant the
        // backdrop silently stopped updating for as long as the card sat
        // there: no error, no blank frame, just glass frozen on whatever the
        // desktop looked like when it arrived. Crop now clamps to the edge
        // instead, so every position on the screen refreshes like any other.
        //
        // The edge case Crop cannot rescue is the card being entirely off
        // screen, which GetWindowRect plus a visible window rules out.

        int generation = ++_backdropGeneration;

        _backdropRenderBusy = true;

        _ = Task.Run(() =>
        {
            byte[]? encoded = null;

            // The size actually produced, which is half the requested one
            // because the blur runs on a downscaled copy.
            int outWidth = width;
            int outHeight = height;
            int outMargin = margin;

            try
            {
                byte[]? pixels = ScreenCapture.Crop(
                    cache,
                    cacheW,
                    cacheH,
                    sourceX,
                    sourceY,
                    width,
                    height);

                if (pixels is null)
                    return;

                // The grab contains this window, so rebuild its rectangle from
                // the genuine border around it.
                //
                // Doing it here rather than on the full screen grab is what
                // keeps it cheap: the crop is only the glass area, and the work
                // is already off the UI thread. It is applied on every crop,
                // including the re-cuts taken from the cache during a drag, so
                // the window's own pixels can never reach the glass no matter
                // which capture the crop came from.
                //
                // The reconstruction is a smooth bilinear ramp, which is why
                // this is only viable because the result is blurred: after a
                // radius 10 blur the filled area reads as broad colour and
                // brightness rather than as a copy of anything.
                ScreenCapture.InpaintRegion(
                    pixels,
                    width,
                    height,
                    margin,
                    margin,
                    windowWidth,
                    windowHeight,
                    InpaintExpandPx,
                    InpaintSkipPx);

                // Blur a half size copy. Everything downstream (blur, BMP
                // encode, decode, upload) then works on a quarter of the
                // pixels, which is what the expanded card needs: its region is
                // about four times the pill's.
                byte[]? small = ScreenCapture.Downscale2(pixels, width, height);

                if (small is null)
                    return;

                // The margin is expressed in the same units as the image, so it
                // halves along with it. Stretch="Fill" scales the small bitmap
                // back up over the same area.
                int smallMargin = margin / 2;

                FastBlur.Blur(
                    small,
                    width / 2,
                    height / 2,
                    BlurRadius / 2,
                    BlurPasses);

                encoded = BmpEncoder.Encode(small, width / 2, height / 2);

                outWidth = width / 2;
                outHeight = height / 2;
                outMargin = smallMargin;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"backdrop render failed: {ex}");
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _backdropRenderBusy = false;

                    // A newer render started while this one was working, so this
                    // result is already out of date.
                    if (encoded is null || generation != _backdropGeneration)
                        return;

                    ApplyBackdropTo(encoded, outWidth, outHeight, outMargin);
                });
            }
        });
    }



    /// <summary>
    /// Queues a backdrop refresh on the next dispatcher pass, so layout and
    /// the window resize have settled before the screen is grabbed.
    /// </summary>
    private void ScheduleBackdropRefresh()
    {
        if (_theme != ThemeMode.LiquidGlass)
            return;

        DispatcherQueue.TryEnqueue(RefreshBackdropFromScreen);
    }


    /// <summary>
    /// Decodes an encoded glass region and only then hands it to the cards.
    ///
    /// The decode MUST complete before Source is assigned. Assigning first and
    /// awaiting afterwards leaves the Image pointing at a freshly constructed,
    /// still empty BitmapImage, so it renders nothing until the decode lands.
    /// Doing that on every refresh was a blank frame roughly twice a second,
    /// which is exactly the flicker that was reported.
    ///
    /// Both cards get the same ImageSource: the pixels are identical, so
    /// encoding and decoding them separately doubled the cost for nothing.
    ///
    /// async void is deliberate: this only sets Image.Source on the UI thread
    /// and swallows failures, so there is nothing useful for a caller to await.
    /// Failures are reported rather than silently ignored, because a silent
    /// catch here hid a broken BMP encoder behind an opaque-looking panel.
    /// </summary>
    private async void ApplyBackdropTo(
        byte[] encoded,
        int width,
        int height,
        int margin)
    {
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();

            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(encoded);
                await writer.StoreAsync();
            }

            stream.Seek(0);

            var image = new BitmapImage();

            // Decode first. Until this returns the image has no content, and
            // binding it to an Image would show a blank frame.
            await image.SetSourceAsync(stream);

            var thickness = new Thickness(-margin, -margin, -margin, -margin);

            CompactBackdropImage.Margin = thickness;
            ExpandedBackdropImage.Margin = thickness;

            // Both are assigned only now, together, so neither ever shows an
            // empty source.
            CompactBackdropImage.Source = image;
            ExpandedBackdropImage.Source = image;

            stream.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"backdrop upload failed: {ex}");
        }
    }



    private void ApplyTheme(ThemeMode theme)
    {
        _theme = theme;

        // Opaque base colour. The compact card sits on a root painted in the
        // same colour, so the card's own corners blend into it and the pill
        // can be genuinely round (DWM alone only reaches 12px here).
        switch (theme)
        {
            case ThemeMode.Material3:
                _compactBase = Color.FromArgb(0xFF, 0x1B, 0x23, 0x36);
                break;

            case ThemeMode.FrostedGlass:
                _compactBase = Color.FromArgb(0xFF, 0x1A, 0x1F, 0x2E);
                break;

            default:
                // Sits a shade below the glass body so its lit top edge and
                // inner rim have something to read against.
                _compactBase = Color.FromArgb(0xFF, 0x0D, 0x11, 0x19);
                break;
        }

        bool glass = theme == ThemeMode.LiquidGlass;

        // The glass pass is layered on top of the card body, so it is only
        // shown for the liquid glass theme. The other two paint a single
        // flat brush and hide the overlays entirely.
        CompactGlass.Visibility =
            glass ? Visibility.Visible : Visibility.Collapsed;

        ExpandedGlass.Visibility =
            glass ? Visibility.Visible : Visibility.Collapsed;

        if (glass)
        {
            // The glass paints its own blurred copy of the desktop, so DWM is
            // deliberately left with no backdrop. Letting acrylic stay on would
            // only stack its opaque FallbackColor underneath the capture.
            //
            // Re-tested on this machine (build 26200.9457, Windows 11 25H2,
            // transparency effects enabled, console session) by setting
            // SystemBackdrop = new DesktopAcrylicBackdrop() with the captured
            // image hidden, so the screenshot showed only what DWM produced.
            // Result: a flat dark tint, with the white desktop behind the card
            // completely absent. Stdev of the interior samples was 26, and that
            // came from the text glyphs, not from any background variation.
            // So this is a window configuration limitation, not a missing OS
            // feature, and the capture pipeline is the only genuine option.
            SystemBackdrop = null;

            CompactBackdropHost.Visibility = Visibility.Visible;
            ExpandedBackdropHost.Visibility = Visibility.Visible;

            CompactCard.Background = CreateGlassTintBrush();
            ExpandedCard.Background = CreateGlassTintBrush();

            ApplyLiquidGlassLayers(
                CompactLighting,
                CompactRim,
                CompactFringe,
                false);

            ApplyLiquidGlassLayers(
                ExpandedLighting,
                ExpandedRim,
                null,
                true);
        }
        else
        {
            // Flat themes have nothing to see through, so the window paints
            // itself and the captured backdrop is dropped.
            SystemBackdrop = null;

            CompactBackdropHost.Visibility = Visibility.Collapsed;
            ExpandedBackdropHost.Visibility = Visibility.Collapsed;

            CompactCard.Background =
                new SolidColorBrush(_compactBase);

            switch (theme)
            {
                case ThemeMode.Material3:
                    ExpandedCard.Background = CreateMaterialCardBrush();
                    break;

                default:
                    ExpandedCard.Background =
                        new SolidColorBrush(_compactBase);
                    break;
            }
        }

        ApplyModeShape();
    }


    /// <summary>
    /// Paints the root and sets the corner radii for the current mode and theme.
    /// The root has to stay clear for the glass theme or the acrylic backdrop
    /// behind it is completely hidden; the flat themes paint it instead,
    /// otherwise the desktop would show straight through the window.
    /// </summary>
    private void ApplyModeShape()
    {
        Root.Background =
            _theme == ThemeMode.LiquidGlass
                ? new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00))
                : new SolidColorBrush(_compactBase);

        if (_expanded)
        {
            ExpandedCard.CornerRadius = new CornerRadius(0);
        }
        else
        {
            // Every layer of the pill shares one radius, and that radius is
            // deliberately SMALLER than the window region's.

            // The region is a true pill at half the height and it is 1-bit, so
            // its edge is a staircase. The card has to be fuller than that cut
            // so the staircase always lands on opaque glass. Matching the two
            // radii instead was tried and is worse: the card's antialiased edge
            // then ramps to zero alpha exactly where the region cuts, and the
            // black window shows through as dark speckles at both left corners.

            // Keeping the card full also keeps the rim and fringe clear of the
            // cut, so they need no inset and can share the one radius below.
            // DWM will not round a 48px tall window further than 8px, but the GDI
            // region supplies the true pill, so the card only has to be full
            // enough to cover that cut.
            CornerRadius radius = new(GlassCompactRadius);

            CompactCard.CornerRadius = radius;
            CompactBackdropHost.CornerRadius = radius;
            CompactLighting.CornerRadius = radius;
            CompactRim.CornerRadius = radius;
            CompactRim.Margin = new Thickness(0);
            CompactFringe.CornerRadius = radius;
            CompactFringe.Margin = new Thickness(0);
        }

        ApplyWindowShape();
    }

    /// <summary>
    /// Half the measured compact height, clamped to something sane before the
    /// first measurement has happened.
    /// </summary>
    /// <summary>
    /// Corner radius shared by the compact card and all four of its glass
    /// overlays.
    ///
    /// This has to stay smaller than the window region's own radius. The
    /// region is 1-bit, so its cut is a staircase, and the card must always be
    /// full enough that the staircase lands on opaque glass rather than on the
    /// antialiased edge fading to transparent, which would let the black
    /// window behind show through as dark speckles.
    /// </summary>
    private const double GlassCompactRadius = 10;

    private double GetCompactPillRadius()
    {
        if (_compactFitHeight > 0)
            return _compactFitHeight / 2d;

        return FallbackCompactPillRadius;
    }

    /// <summary>
    /// Specular lighting. Kept very light on purpose: every extra layer of white
    /// here is opacity the background cannot show through.
    ///
    /// The expanded card needs a much weaker veil than the pill. This is the
    /// same gradient on both, but on a 48px tall pill the white only reads as a
    /// sheen along the top edge, whereas spread over a 210px card it washes
    /// across the large saturated metric text and makes it look like it is
    /// glowing. So the expanded variant keeps the shape and drops the
    /// intensity by roughly two thirds.
    /// </summary>
    private static Brush CreateGlassLightingBrush(bool expanded)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };

        if (expanded)
        {
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), Offset = 0.00 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), Offset = 0.20 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 0.45 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 0.78 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x0A, 0x00, 0x00, 0x00), Offset = 1.00 });

            return brush;
        }

        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Offset = 0.00 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), Offset = 0.20 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 0.45 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 0.78 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x12, 0x00, 0x00, 0x00), Offset = 1.00 });

        return brush;
    }


    /// <summary>
    /// The inner stroke rim: bright along the top where the light falls and
    /// gone by the bottom edge. This single detail does more to sell "glass"
    /// than the body colour does.
    /// </summary>
    private static Brush CreateGlassRimBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };

        // Neutral white, and much weaker than it was.
        //
        // This was 0xD9DDF3FF, a blue tinted white at 85% alpha, which put a
        // distinctly blue band across the top of the pill. The rim is meant to
        // be light falling on an edge, so the colour has to be neutral; any
        // tint in it reads as a coloured glow rather than as glass.
        //
        // The top stop is also the pixel sitting directly against the 1-bit
        // region cut, so it is kept soft. A near opaque stop there turns every
        // step of the staircase into a visible bright notch.
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), Offset = 0.00 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), Offset = 0.18 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF), Offset = 0.44 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), Offset = 0.72 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1.00 });

        return brush;
    }


    /// <summary>
    /// Chromatic aberration. The reference shader splits the light refracted
    /// through the rim into colour fringes; a cool tint on one side and a
    /// warm one on the other is a cheap stand-in for it.
    /// </summary>
    private static Brush CreateGlassFringeBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };

        // Barely there. This was a saturated 0x4DC6FF on the left and a pink
        // on the right at a fairly high alpha, which is what produced the blue
        // bleeding down the left edge. Chromatic aberration is a few percent
        // of the rim, not a visible colour cast: the hues are kept but the
        // alpha is cut to roughly a fifth of what it was.
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x0D, 0x6E, 0xA8, 0xE0), Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x09, 0xE8, 0x9C, 0xC4), Offset = 1.0 });

        return brush;
    }


    /// <summary>
    /// Points the three overlay layers at their brushes. The fringe is only
    /// used on the pill, where the whole rim is visible.
    /// </summary>
    private void ApplyLiquidGlassLayers(
        Border lighting,
        Border rim,
        Border? fringe,
        bool expanded)
    {
        lighting.Background = CreateGlassLightingBrush(expanded);

        rim.BorderBrush = CreateGlassRimBrush();

        if (fringe is not null)
            fringe.BorderBrush = CreateGlassFringeBrush();
    }

    private static Brush CreateMaterialCardBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };

        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xE1, 0x1B, 0x23, 0x36), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xE1, 0x1A, 0x1F, 0x2D), Offset = 0.55 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xE1, 0x1C, 0x22, 0x30), Offset = 1 });

        return brush;
    }

    private void InitializeGpuCounter()
    {
        const uint PdhSuccess = 0;

        if (PdhOpenQueryW(IntPtr.Zero, UIntPtr.Zero, out _gpuQuery) != PdhSuccess)
        {
            _gpuQuery = IntPtr.Zero;
            return;
        }

        if (PdhAddEnglishCounterW(
                _gpuQuery,
                @"\GPU Engine(*)\Utilization Percentage",
                UIntPtr.Zero,
                out _gpuCounter) != PdhSuccess)
        {
            PdhCloseQuery(_gpuQuery);
            _gpuQuery = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
            return;
        }

        PdhCollectQueryData(_gpuQuery);
    }

    /// <summary>
    /// Sets a usage bar to the given 0-100 percentage by sizing the fill
    /// border. WinUI 3's ProgressBar cannot be used here: instantiating it
    /// crashes the XAML framework at startup on this runtime.
    /// </summary>
    private static void SetBar(Border bar, double percent)
    {
        double clamped = Math.Clamp(percent, 0, 100);
        double track = bar.Parent is FrameworkElement parent ? parent.ActualWidth : 0;

        if (track <= 0)
        {
            bar.Width = 0;
            return;
        }

        bar.Width = track * (clamped / 100.0);
    }

    private void UpdateResourceUsage()
    {
        UpdateCpuUsage();
        UpdateMemoryUsage();

        if (TryGetGpuUsage(out double gpuUsage))
        {
            gpuUsage = Math.Clamp(gpuUsage, 0, 100);
            GpuUsageText.Text = $"{gpuUsage:0}%";
            SetBar(GpuUsageBar, gpuUsage);
        }
        else
        {
            GpuUsageText.Text = "N/A";
            SetBar(GpuUsageBar, 0);
        }
    }

    private void UpdateCpuUsage()
    {
        if (!GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user))
        {
            CpuUsageText.Text = "N/A";
            SetBar(CpuUsageBar, 0);
            return;
        }

        ulong idleTicks = ToUInt64(idle);
        ulong totalTicks = ToUInt64(kernel) + ToUInt64(user);

        if (!_cpuInitialized)
        {
            _lastCpuIdle = idleTicks;
            _lastCpuTotal = totalTicks;
            _cpuInitialized = true;
            return;
        }

        ulong totalDelta = totalTicks - _lastCpuTotal;
        ulong idleDelta = idleTicks - _lastCpuIdle;
        _lastCpuIdle = idleTicks;
        _lastCpuTotal = totalTicks;

        double usage = totalDelta == 0
            ? 0
            : Math.Clamp(100.0 * (totalDelta - Math.Min(idleDelta, totalDelta)) / totalDelta, 0, 100);

        CpuUsageText.Text = $"{usage:0}%";
        SetBar(CpuUsageBar, usage);
    }

    private void UpdateMemoryUsage()
    {
        var status = new MemoryStatus();
        if (!GlobalMemoryStatusEx(status))
        {
            MemoryUsageText.Text = "N/A";
            SetBar(MemoryUsageBar, 0);
            return;
        }

        MemoryUsageText.Text = $"{status.MemoryLoad}%";
        SetBar(MemoryUsageBar, status.MemoryLoad);
    }

    private bool TryGetGpuUsage(out double usage)
    {
        usage = 0;
        if (_gpuQuery == IntPtr.Zero || _gpuCounter == IntPtr.Zero ||
            PdhCollectQueryData(_gpuQuery) != 0)
            return false;

        const uint PdhMoreData = 0x800007D2;
        const uint PdhFormatDouble = 0x00000200;
        uint bufferSize = 0;
        uint itemCount = 0;
        uint result = PdhGetFormattedCounterArray(
            _gpuCounter,
            PdhFormatDouble,
            ref bufferSize,
            ref itemCount,
            IntPtr.Zero);

        if (result != PdhMoreData || bufferSize == 0 || bufferSize > int.MaxValue || itemCount > 4096)
            return false;

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            result = PdhGetFormattedCounterArray(
                _gpuCounter,
                PdhFormatDouble,
                ref bufferSize,
                ref itemCount,
                buffer);

            if (result != 0 || itemCount == 0)
                return false;

            int itemSize = Marshal.SizeOf<PdhFormattedItem>();
            bool hasValue = false;
            double busiestEngine = 0;

            for (uint index = 0; index < itemCount; index++)
            {
                var item = Marshal.PtrToStructure<PdhFormattedItem>(
                    IntPtr.Add(buffer, checked((int)index * itemSize)));

                // PDH_CSTATUS_VALID_DATA (0) and PDH_CSTATUS_NEW_DATA (1) are the
                // only statuses that carry a meaningful value.
                if ((item.Value.Status is 0 or 1) &&
                    double.IsFinite(item.Value.DoubleValue))
                {
                    busiestEngine = Math.Max(busiestEngine, item.Value.DoubleValue);
                    hasValue = true;
                }
            }

            usage = busiestEngine;
            return hasValue;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ulong ToUInt64(NativeFileTime value) =>
        ((ulong)value.High << 32) | value.Low;

    private void ToggleAlwaysOnTop()
    {
        _alwaysOnTop = !_alwaysOnTop;

        SetWindowPos(
            _hwnd,
            _alwaysOnTop
                ? HWND_TOPMOST
                : HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE |
            SWP_NOSIZE |
            SWP_SHOWWINDOW);
    }

    private void SetupWindow()
    {
        var windowId =
            Win32Interop.GetWindowIdFromWindow(_hwnd);


        var appWindow =
            AppWindow.GetFromWindowId(windowId);


        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }


        appWindow.Title = "";

        RemoveWindowFrame();
        ApplyWindowShape();
    }


    /// <summary>
    /// Strips the non-client frame (caption, dialog frame, sysmenu, min/max).
    /// SetBorderAndTitleBar(false, false) does not do this for a WinUI 3
    /// window, so a ~3px white border was left around the client area. With
    /// the frame gone the client area equals the window, the card fills it, and
    /// the rounded region clips the whole thing cleanly.
    /// </summary>
    private void RemoveWindowFrame()
    {
        if (_hwnd == 0)
            return;

        int style = GetWindowLong(_hwnd, GWL_STYLE);

        style &= ~(WS_CAPTION |
                   WS_THICKFRAME |
                   WS_DLGFRAME |
                   WS_SYSMENU |
                   WS_MINIMIZEBOX |
                   WS_MAXIMIZEBOX);

        style |= WS_POPUP;

        SetWindowLong(_hwnd, GWL_STYLE, style);
    }



    /// <summary>
    /// Applies the window shape for the current mode.
    ///
    /// Compact is shaped by a window region so it can be a true pill, because
    /// DWM rounding caps at 12px and reads as a rounded rectangle on a bar
    /// this short. Expanded falls back to DWM's own antialiased rounding.
    ///
    /// The two must not be active at the same time: DWM rounds against the
    /// window rect and ignores the region, so leaving it on in compact mode
    /// paints a 12px rounded rectangle just outside the pill, which is what
    /// produced the light halo around the corners.
    /// </summary>
    private void ApplyWindowShape()
    {
        if (_hwnd == 0)
            return;

        // Compact is shaped by a real window region so it can be a true 24px
        // pill. DWM only reaches 8px here, and that mismatch is not cosmetic:
        // the card draws its own 24px rounded image, so the 16px left over at
        // each corner showed the opaque black window behind it as dark corner
        // blocks. Matching the two removes them.
        //
        // The glass theme used to be excluded from this because a GDI region
        // does not clip the DWM backdrop material, and glass was using
        // SystemBackdrop. It no longer does: the glass paints its own captured
        // and blurred image, which a region clips correctly.
        int corner =
            _expanded
                ? DWMWCP_ROUNDLARGE
                : DWMWCP_NONE;

        DwmSetWindowAttribute(
            _hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            ref corner,
            sizeof(int));

        // Windows 11 adds its own subtle border to rounded windows, which
        // draws a light outline outside the pill. Suppress it so the only
        // visible edge is the one being asked for.
        int border = DWMWA_COLOR_NONE;

        DwmSetWindowAttribute(
            _hwnd,
            DWMWA_BORDER_COLOR,
            ref border,
            sizeof(int));

        // The 1-bit region has to stay. The window paints opaque black outside
        // the card, so without it the corners fill with black blocks. Removing
        // the class background brush was tried to make the window itself
        // transparent instead, and it made no difference: the black comes from
        // the XAML composition surface, not from the class brush. What makes
        // the edge acceptable is that every XAML layer now shares the region's
        // own radius, so the cut lands on an already transparent curve.
        if (_expanded)
            ClearCompactPillRegion();
        else
            ApplyCompactPillRegion();
    }


    /// <summary>
    /// Clips the compact window to a true pill. DWM cannot round further than
    /// 12px, so on a 48px tall bar it reads as a rounded rectangle rather than
    /// a pill, and the extra rounding has to come from a window region.
    /// The trade-off is that a GDI region is 1-bit, so the curve is stepped
    /// rather than antialiased; DWM is left enabled to smooth what it can
    /// inside the region. Remove the call in ResizeWindow to go back to
    /// smooth 12px corners.
    /// </summary>
    private void ApplyCompactPillRegion()
    {
        if (_hwnd == 0 || _expanded)
            return;

        if (!GetWindowRect(_hwnd, out RECT rect))
            return;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            return;

        double scale = GetWindowDpiScale();

        int diameter =
            (int)Math.Round(GetCompactPillRadius() * 2d * scale);

        // CreateRoundRectRgn behaves badly if the ellipse is larger than the
        // rectangle it is meant to carve out of.
        diameter =
            Math.Min(
                diameter,
                Math.Min(width, height));

        nint region =
            CreateRoundRectRgn(
                0,
                0,
                width,
                height,
                diameter,
                diameter);

        if (region == 0)
            return;

        // The window takes ownership of a successfully applied region, so it
        // must not be deleted here.
        SetWindowRgn(_hwnd, region, true);
    }


    /// <summary>
    /// Drops the pill region so the expanded dashboard falls back to DWM's own
    /// rounding across the full window.
    /// </summary>
    private void ClearCompactPillRegion()
    {
        if (_hwnd == 0)
            return;

        SetWindowRgn(_hwnd, 0, true);
    }


    private void Root_DoubleTapped(
        object? sender,
        DoubleTappedRoutedEventArgs e)
    {
        _dragging = false;
        _ignoreNextDrag = true;

        if (_expanded)
            ShowCompact();
        else
            ShowExpanded();

        e.Handled = true;
    }





    private void Root_RightTapped(
        object? sender,
        RightTappedRoutedEventArgs e)
    {
        MenuFlyout menu = new();


        MenuFlyoutItem compact =
            new()
            {
                Text = "Compact Mode"
            };

        compact.Click += (_, __) =>
        {
            ShowCompact();
        };


        MenuFlyoutItem expanded =
            new()
            {
                Text = "Expanded Mode"
            };

        expanded.Click += (_, __) =>
        {
            ShowExpanded();
        };


        MenuFlyoutItem always =
            new()
            {
                Text = _alwaysOnTop
                    ? "Disable Always On Top"
                    : "Enable Always On Top"
            };

        always.Click += (_, __) =>
        {
            ToggleAlwaysOnTop();
        };

        MenuFlyoutSeparator themeSeparator = new();

        MenuFlyoutItem liquidGlass =
            new()
            {
                Text = "Theme: Liquid Glass"
            };

        liquidGlass.Click += (_, __) =>
        {
            ApplyTheme(ThemeMode.LiquidGlass);
        };

        MenuFlyoutItem material3 =
            new()
            {
                Text = "Theme: Material 3 Expressive"
            };

        material3.Click += (_, __) =>
        {
            ApplyTheme(ThemeMode.Material3);
        };

        MenuFlyoutItem frostedGlass =
            new()
            {
                Text = "Theme: Frosted Glass"
            };

        frostedGlass.Click += (_, __) =>
        {
            ApplyTheme(ThemeMode.FrostedGlass);
        };

        MenuFlyoutSeparator separator = new();


        MenuFlyoutItem close =
            new()
            {
                Text = "Close"
            };

        close.Click += (_, __) =>
        {
            Close();
        };


        menu.Items.Add(compact);
        menu.Items.Add(expanded);
        menu.Items.Add(always);
        menu.Items.Add(themeSeparator);
        menu.Items.Add(liquidGlass);
        menu.Items.Add(material3);
        menu.Items.Add(frostedGlass);
        menu.Items.Add(separator);
        menu.Items.Add(close);


        menu.ShowAt(
            Root,
            e.GetPosition(Root));
    }





    private void Root_PointerPressed(
        object? sender,
        PointerRoutedEventArgs e)
    {
        if (_ignoreNextDrag)
        {
            _ignoreNextDrag = false;
            return;
        }

        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
            return;

        if (!GetCursorPos(out _mouseStart))
            return;

        _windowStart = AppWindow.Position;

        _dragOffset = new Point(
            _mouseStart.X - _windowStart.X,
            _mouseStart.Y - _windowStart.Y);

        // Arm the drag but do not begin it. Without this threshold a press
        // followed by a couple of pixels of jitter snapped the window to the
        // cursor, which felt like the pill was glued to the pointer.
        _pressing = true;
        _dragging = false;

        if (sender is UIElement element)
            element.CapturePointer(e.Pointer);

        e.Handled = true;
    }


    private void Root_PointerMoved(
        object? sender,
        PointerRoutedEventArgs e)
    {
        if (!_pressing)
            return;

        if (!GetCursorPos(out POINT now))
            return;

        if (!_dragging)
        {
            double dx = now.X - _mouseStart.X;
            double dy = now.Y - _mouseStart.Y;

            if (Math.Abs(dx) < DragThreshold &&
                Math.Abs(dy) < DragThreshold)
                return;

            _dragging = true;
        }

        AppWindow.Move(
            new PointInt32(
                (int)(now.X - _dragOffset.X),
                (int)(now.Y - _dragOffset.Y)));

        // Track the desktop under the widget while it moves. This only re-cuts
        // the already captured screen, so the background follows the drag
        // smoothly instead of jumping when the drag ends, and it cannot
        // flicker because nothing is clipped and the screen is not read.
        //
        // Throttled, and skipped entirely while a previous render is still in
        // flight, so a slow pass costs frame rate rather than queueing up.
        if (_theme != ThemeMode.LiquidGlass)
            return;

        int tick = Environment.TickCount;

        if (tick - _lastDragRenderTick < DragRenderIntervalMs)
            return;

        _lastDragRenderTick = tick;

        QueueBackdropRender();
    }






    private void SetInitialPosition()
    {
        if (_initialPositionSet)
            return;

        var display =
            DisplayArea.GetFromWindowId(
                AppWindow.Id,
                DisplayAreaFallback.Primary);

        var work = display.WorkArea;

        AppWindow.Move(
            new PointInt32(
                work.X + work.Width - AppWindow.Size.Width - 18,
                work.Y + work.Height - AppWindow.Size.Height - 18));

        _initialPositionSet = true;
    }


    private void ShowCompact()
    {
        _expanded = false;

        CompactCard.Visibility =
            Visibility.Visible;

        ExpandedCard.Visibility =
            Visibility.Collapsed;

        ApplyModeShape();

        // Measured on a previous visit: apply the cached size straight away so
        // toggling back to compact is instant rather than waiting on a timer.
        if (_compactFitWidth > 0)
        {
            ResizeWindow(_compactFitWidth, _compactFitHeight);
            SetInitialPosition();
            ScheduleBackdropRefresh();
            return;
        }

        // First run only: nothing is cached yet, so measure it once.
        ScheduleCompactFit();
    }


    /// <summary>
    /// Applies the previously measured compact size immediately. Only the very
    /// first switch needs the deferred measurement, so subsequent toggles to
    /// compact are instant and do not feel laggy.
    /// </summary>
    private void ScheduleCompactFit()
    {
        if (_compactFitWidth > 0 && _compactFitHeight > 0)
        {
            ResizeWindow(_compactFitWidth, _compactFitHeight);
            SetInitialPosition();
            return;
        }

        // First run only. Open the window up to a size that is guaranteed to
        // hold the widest strings, then measure on a later dispatcher turn once
        // XAML has actually laid the content out at that size. Measuring in the
        // same turn as the resize reads a stale, already-squeezed box and the
        // pill ends up too small to show the text.
        ResizeWindow(CompactProbeWidth, CompactProbeHeight);

        StartFitTimer(BeginCompactFit);
    }


    private void StartFitTimer(Action action)
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            action();
        };

        timer.Start();
    }


    // Widest string the rate can render. The pill is sized for this so its
    // width never jitters as the live values come and go.
    private const string WidestDown = "↓ 999.9 KB/s";
    private const string WidestUp = "↑ 999.9 KB/s";

    private const double CompactPadX = 14;
    private const double CompactPadY = 15;

    // Placeholder until the real compact height has been measured.
    private const double FallbackCompactPillRadius = 24;

    // Deliberately oversized frame used only while measuring, so the widest
    // strings are never trimmed and the measurement is truthful.
    private const int CompactProbeWidth = 420;
    private const int CompactProbeHeight = 60;


    /// <summary>
    /// Shows the widest possible strings and waits one more turn so they are
    /// laid out, then reads the resulting content size.
    /// </summary>
    private void BeginCompactFit()
    {
        _compactProbeDown = CompactDownloadText.Text;
        _compactProbeUp = CompactUploadText.Text;

        CompactDownloadText.Text = WidestDown;
        CompactUploadText.Text = WidestUp;

        CompactCard.UpdateLayout();

        StartFitTimer(FinishCompactFit);
    }


    private void FinishCompactFit()
    {
        try
        {
            // Now that the widest strings have been laid out inside a window
            // that is comfortably larger than they need, this is the real size.
            double contentWidth = CompactContent.ActualWidth;
            double contentHeight = CompactContent.ActualHeight;

            if (contentWidth <= 0 || contentHeight <= 0)
            {
                _compactFitWidth = 0;
                _compactFitHeight = 0;
                return;
            }

            _compactFitWidth =
                (int)Math.Ceiling(contentWidth + (CompactPadX * 2));

            _compactFitHeight =
                (int)Math.Ceiling(contentHeight + (CompactPadY * 2));

            // The pill radius is derived from the height, so refresh it now
            // that the real height is known.
            ApplyModeShape();

            ResizeWindow(_compactFitWidth, _compactFitHeight);
        }
        finally
        {
            CompactDownloadText.Text = _compactProbeDown;
            CompactUploadText.Text = _compactProbeUp;
        }
    }





    private void ShowExpanded()
    {
        _expanded = true;

        CompactCard.Visibility =
            Visibility.Collapsed;

        ExpandedCard.Visibility =
            Visibility.Visible;

        ApplyModeShape();

        ResizeWindow(480, 210);

        ScheduleBackdropRefresh();
    }




    /// <summary>
    /// Resizes the window. Width/height are in DIPs, but AppWindow.Resize()
    /// takes physical pixels, so they are converted using the current
    /// rasterization scale. Without this, a 480x210 DIP window on a 150%
    /// display is created as 480x210 physical px, which the XAML layout only
    /// sees as 320x140 DIP, and the content overflows and is clipped.
    /// </summary>
    private void ResizeWindow(
        int width,
        int height)
    {
        var id =
            Win32Interop.GetWindowIdFromWindow(_hwnd);


        var window =
            AppWindow.GetFromWindowId(id);


        double scale = GetWindowDpiScale();

        window.Resize(
            new SizeInt32(
                (int)Math.Round(width * scale),
                (int)Math.Round(height * scale)));

        // Re-apply after every resize so the shape tracks the new size.
        ApplyWindowShape();
    }


    /// <summary>
    /// DIP -> physical pixel scale for this window's monitor.
    /// Uses GetDpiForWindow rather than XamlRoot.RasterizationScale because
    /// the first resize happens in the constructor, before the window is
    /// realised and XamlRoot is still null.
    /// </summary>
    private double GetWindowDpiScale()
    {
        uint dpi =
            _hwnd != 0
                ? GetDpiForWindow(_hwnd)
                : 0;

        double scale =
            dpi > 0
                ? dpi / 96d
                : 1d;

        if (double.IsNaN(scale) || scale <= 0)
            scale = 1d;

        return scale;
    }






    private void Timer_Tick(
        object? sender,
        object? e)
    {
        UpdateResourceUsage();

        var data =
            GetNetworkTotals();


        DateTime now =
            DateTime.UtcNow;


        double seconds =
            Math.Max(
                (now - _lastSample).TotalSeconds,
                .001);



        if (_firstSample || _adapterChanged)
        {
            _lastSent = data.sent;
            _lastReceived = data.received;
            _lastSample = now;
            _firstSample = false;
            _adapterChanged = false;

            // Show an explicit zero rather than leaving the XAML placeholder.
            CompactUploadText.Text = "↑ 0.0 KB/s";
            CompactDownloadText.Text = "↓ 0.0 KB/s";
            UploadText.Text = "↑ 0.0 KB/s";
            DownloadText.Text = "↓ 0.0 KB/s";
            return;
        }



        double up =
            Math.Max(
                0,
                (data.sent - _lastSent) / seconds);



        double down =
            Math.Max(
                0,
                (data.received - _lastReceived) / seconds);



        string upload =
            $"↑ {FormatRate(up)}";


        string download =
            $"↓ {FormatRate(down)}";



        CompactUploadText.Text = upload;
        CompactDownloadText.Text = download;

        UploadText.Text = upload;
        DownloadText.Text = download;





        _lastSent = data.sent;
        _lastReceived = data.received;
        _lastSample = now;
    }






    private void Root_PointerReleased(
        object? sender,
        PointerRoutedEventArgs e)
    {
        _pressing = false;
        _dragging = false;

        if (sender is UIElement element)
            element.ReleasePointerCapture(e.Pointer);

        // The widget may have moved across the desktop, so the captured
        // backdrop is now stale.
        ScheduleBackdropRefresh();

        e.Handled = true;
    }






    private static NetworkInterface? GetActiveAdapter()
    {
        return NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .OrderByDescending(n => n.Speed)
            .FirstOrDefault();
    }


    private void PrimeNetworkBaseline()
    {
        var adapter =
            GetActiveAdapter();

        if (adapter == null)
            return;

        _adapterId =
            adapter.Id;

        try
        {
            var stats =
                adapter.GetIPv4Statistics();

            _lastSent = stats.BytesSent;
            _lastReceived = stats.BytesReceived;
        }
        catch
        {
            // Leave the baseline at zero; the first tick re-primes it.
        }

        _lastSample = DateTime.UtcNow;
    }


    private (long sent, long received) GetNetworkTotals()
    {
        var adapter =
            GetActiveAdapter();

        if (adapter == null)
            return (0, 0);

        // A different adapter means the byte counters are unrelated, so the
        // delta would be a huge bogus rate. Re-prime instead.
        if (_adapterId != adapter.Id)
        {
            _adapterId = adapter.Id;
            _adapterChanged = true;
        }

        try
        {
            var stats = adapter.GetIPv4Statistics();
            return (stats.BytesSent, stats.BytesReceived);
        }
        catch
        {
            return (0, 0);
        }
    }



    private async Task UpdateNetworkInfo()
    {
        try
        {
            using Ping ping = new();

            var result = await ping.SendPingAsync("8.8.8.8", 2000);

            if (result.Status == IPStatus.Success)
            {
                PingText.Text =
                    $"Ping {result.RoundtripTime} ms";
            }
            else
            {
                PingText.Text = "Ping unavailable";
            }


            var adapter =
                GetActiveAdapter();


            if (adapter != null)
            {
                // Description is the hardware name
                // ("Intel(R) Wi-Fi 6 AX201 160MHz"); Name is only the
                // connection profile and usually just says "Wi-Fi".
                string description = adapter.Description ?? string.Empty;
                string name = adapter.Name ?? string.Empty;

                string label =
                    !string.IsNullOrWhiteSpace(description)
                        ? description
                        : name;

                if (string.IsNullOrWhiteSpace(label))
                    label = adapter.NetworkInterfaceType.ToString();

                AdapterText.Text = label;
            }
            else
            {
                AdapterText.Text = "No adapter";
            }
        }
        catch
        {
            PingText.Text = "Ping unavailable";
        }
    }


    private static string FormatRate(double value)
    {
        const double Kb = 1024d;
        const double Mb = 1024d * 1024d;

        // KB/s is the floor: sub-kilobyte rates are noise on a speedometer and
        // the shorter string also stops the value being clipped.
        if (value >= Mb)
            return $"{value / Mb:0.0} MB/s";

        return $"{value / Kb:0.0} KB/s";
    }
}
