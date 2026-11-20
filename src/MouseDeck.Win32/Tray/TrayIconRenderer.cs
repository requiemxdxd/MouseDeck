using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MouseDeck.Core.Models;
using MouseDeck.Win32.Windowing;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace MouseDeck.Win32.Tray;

/// <summary>
/// Renders razor-sharp DirectWrite + WPF hardware-vector bitmaps for:
///  1. Native Windows 11 Taskbar Pill widget ([🖱 30%] / [MAD 8K 30%]) at exact monitor DPI
///     (same DirectWrite TextFormattingMode.Display rasterizer as Windows 11's own taskbar clock/IME).
///  2. Reworked crisp System Tray icons (TrayLargePercent, TrayMouseBatteryFill, TrayRingBadge).
/// Automatically adapts foreground ink to Windows 11 dark/light taskbar theme.
/// </summary>
public static class TrayIconRenderer
{
    private static readonly Typeface SegoeUiVariableSemiBold = new(
        new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static readonly Typeface SegoeUiVariableRegular = new(
        new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    private static readonly Typeface SegoeUiBold = new(
        new System.Windows.Media.FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public static int GetSystemTrayIconSize()
    {
        uint dpi = FlyoutPositioner.GetDpiForSystem();
        return dpi switch
        {
            >= 192 => 32,
            >= 144 => 24,
            >= 120 => 20,
            _ => 16
        };
    }

    public static double GetSystemDpiScale()
    {
        uint dpi = FlyoutPositioner.GetDpiForSystem();
        return dpi >= 96 ? dpi / 96.0 : 1.0;
    }

    public static double GetWindowDpiScale(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi >= 96)
                    return dpi / 96.0;
            }
            catch { }
        }

        return GetSystemDpiScale();
    }

    /// <summary>
    /// Reads HKCU Personalize\SystemUsesLightTheme (0 = Dark Taskbar, 1 = Light Taskbar).
    /// </summary>
    public static bool IsLightTaskbarTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int val)
                return val == 1;
        }
        catch { }
        return false;
    }

    public static IntPtr RenderIcon(MouseRuntimeState state, TrayIconStyle style)
    {
        int size = GetSystemTrayIconSize();
        using var bmp = RenderTrayBitmap(state, style, size);
        return bmp.GetHicon();
    }

    /// <summary>
    /// Renders a crisp DirectWrite + vector square System Tray icon (16x16..32x32)
    /// for TrayLargePercent, TrayMouseBatteryFill, or TrayRingBadge.
    /// </summary>
    public static Bitmap RenderTrayBitmap(MouseRuntimeState state, TrayIconStyle style, int size)
    {
        bool lightTaskbar = IsLightTaskbarTheme();
        var effectiveStyle = style switch
        {
            TrayIconStyle.TrayMouseBatteryFill => TrayIconStyle.TrayMouseBatteryFill,
            TrayIconStyle.TrayRingBadge => TrayIconStyle.TrayRingBadge,
            _ => state.BatteryPercent.HasValue ? TrayIconStyle.TrayLargePercent : TrayIconStyle.TrayMouseBatteryFill
        };

        var visual = CreateCrispDrawingVisual();
        double s = size;

        using (var dc = visual.RenderOpen())
        {
            switch (effectiveStyle)
            {
                case TrayIconStyle.TrayMouseBatteryFill:
                    DrawTrayBatteryFilledMouse(dc, s, state, lightTaskbar);
                    break;
                case TrayIconStyle.TrayRingBadge:
                    DrawTrayRingBadge(dc, s, state, lightTaskbar);
                    break;
                default:
                    DrawTrayLargePercent(dc, s, state, lightTaskbar);
                    break;
            }
        }

        return RenderVisualToPremultipliedBitmap(visual, size, size, 96.0, applyHitTestFloor: false, hitInsetPx: 0);
    }

    /// <summary>
    /// Renders a razor-sharp DirectWrite premultiplied-alpha (Format32bppPArgb) bitmap for the native
    /// Windows 11 Taskbar Pill widget ([🖱 30%] or [MAD 8K 30%]) at native monitor DPI without any bicubic blur.
    /// </summary>
    public static Bitmap RenderTaskbarPillBitmap(
        MouseRuntimeState state,
        TrayIconStyle style,
        double dpiScale,
        bool isHovered,
        bool isPressed)
    {
        bool lightTaskbar = IsLightTaskbarTheme();
        bool showName = style == TrayIconStyle.TaskbarPillNameAndPercent;
        bool hasBattery = state.BatteryPercent.HasValue && state.Status != MouseConnectionStatus.Disconnected;

        string shortName = FormatCompactDeviceLabel(state.DeviceName);
        string mainText = state.Status == MouseConnectionStatus.Disconnected
            ? "—"
            : hasBattery
                ? $"{Math.Clamp(state.BatteryPercent!.Value, 0, 100)}%"
                : "USB";

        double scale = Math.Clamp(dpiScale, 1.0, 2.5);
        double dpi = 96.0 * scale;
        const double dipHeight = 40.0;

        bool showBolt = state.IsCharging;

        WpfColor primaryInk = ResolveInkColor(state, lightTaskbar, isPercentage: false);
        WpfColor percentInk = ResolveInkColor(state, lightTaskbar, isPercentage: true);
        WpfColor mutedInk = lightTaskbar
            ? WpfColor.FromArgb(185, 24, 24, 27)
            : WpfColor.FromArgb(190, 237, 237, 237);

        var mainFt = new FormattedText(
            mainText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            SegoeUiVariableSemiBold,
            12.5,
            CreateFrozenBrush(percentInk),
            null,
            TextFormattingMode.Display,
            scale);

        FormattedText? nameFt = null;
        if (showName && !string.IsNullOrWhiteSpace(shortName))
        {
            nameFt = new FormattedText(
                shortName,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                SegoeUiVariableRegular,
                12.0,
                CreateFrozenBrush(mutedInk),
                null,
                TextFormattingMode.Display,
                scale);
        }

        const double padX = 8.0;
        const double iconW = 12.0;
        const double iconH = 16.0;
        const double gap = 6.0;
        double boltExtra = showBolt ? 8.5 : 0.0;

        double contentW = nameFt is not null
            ? Math.Ceiling(nameFt.Width) + gap + Math.Ceiling(mainFt.Width) + boltExtra
            : iconW + gap + Math.Ceiling(mainFt.Width) + boltExtra;

        double dipWidth = Math.Ceiling(padX * 2.0 + contentW);
        int pixelW = Math.Max(32, (int)Math.Ceiling(dipWidth * scale));
        int pixelH = Math.Max(28, (int)Math.Round(dipHeight * scale));

        // Align logical DIP width exactly to pixel grid
        dipWidth = pixelW / scale;
        double actualDipH = pixelH / scale;

        var visual = CreateCrispDrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Native Windows 11 taskbar hover/press pill backdrop
            if (isHovered || isPressed)
            {
                WpfColor fillCol = lightTaskbar
                    ? WpfColor.FromArgb((byte)(isPressed ? 26 : 16), 0, 0, 0)
                    : WpfColor.FromArgb((byte)(isPressed ? 28 : 18), 255, 255, 255);
                WpfColor borderCol = lightTaskbar
                    ? WpfColor.FromArgb(24, 0, 0, 0)
                    : WpfColor.FromArgb(26, 255, 255, 255);

                double inset = 2.0;
                var pillRect = new WpfRect(inset, inset, dipWidth - inset * 2.0, actualDipH - inset * 2.0);
                var halfPx = 0.5 / scale;
                var gs = new GuidelineSet(
                    [pillRect.Left + halfPx, pillRect.Right - halfPx],
                    [pillRect.Top + halfPx, pillRect.Bottom - halfPx]);
                dc.PushGuidelineSet(gs);
                dc.DrawRoundedRectangle(
                    CreateFrozenBrush(fillCol),
                    CreateFrozenPen(borderCol, 1.0 / scale),
                    pillRect,
                    5.0,
                    5.0);
                dc.Pop();
            }

            double cursorX = padX;
            double centerY = actualDipH / 2.0;

            if (nameFt is not null)
            {
                double ny = Math.Round((centerY - nameFt.Height / 2.0) * scale) / scale;
                dc.DrawText(nameFt, new WpfPoint(Math.Round(cursorX * scale) / scale, ny));
                cursorX += Math.Ceiling(nameFt.Width) + gap;
            }
            else
            {
                double mx = Math.Round(cursorX * scale) / scale;
                double my = Math.Round((centerY - iconH / 2.0) * scale) / scale;
                DrawVectorMouseGlyph(
                    dc, mx, my, iconW, iconH, scale,
                    primaryInk,
                    strokeWidth: 1.25,
                    batteryPercent: state.BatteryPercent,
                    isCharging: state.IsCharging,
                    fillInterior: false,
                    lightTaskbar: lightTaskbar);
                cursorX += iconW + gap;
            }

            double tx = Math.Round(cursorX * scale) / scale;
            double ty = Math.Round((centerY - mainFt.Height / 2.0) * scale) / scale;
            dc.DrawText(mainFt, new WpfPoint(tx, ty));
            cursorX += Math.Ceiling(mainFt.Width);

            if (showBolt)
            {
                WpfColor boltColor = lightTaskbar
                    ? WpfColor.FromRgb(22, 163, 74)
                    : WpfColor.FromRgb(76, 195, 138);
                DrawVectorLightningBolt(dc, cursorX + 2.0, centerY - 5.0, 10.0, boltColor);
            }
        }

        return RenderVisualToPremultipliedBitmap(
            visual,
            pixelW,
            pixelH,
            dpi,
            applyHitTestFloor: true,
            hitInsetPx: 0);
    }

    /// <summary>
    /// Pure 16x16..32x32 Tray Mode 1: DirectWrite crisp large battery digits + 2px semantic battery bar.
    /// </summary>
    private static void DrawTrayLargePercent(DrawingContext dc, double s, MouseRuntimeState state, bool lightTaskbar)
    {
        bool disconnected = state.Status == MouseConnectionStatus.Disconnected || state.BatteryPercent is null;
        if (disconnected)
        {
            DrawTrayBatteryFilledMouse(dc, s, state, lightTaskbar);
            return;
        }

        int pct = Math.Clamp(state.BatteryPercent!.Value, 0, 100);
        string text = pct >= 100 ? "100" : pct.ToString();
        WpfColor textColor = ResolveInkColor(state, lightTaskbar, isPercentage: true);
        WpfColor accentBar = ResolveBarColor(state, lightTaskbar);

        double barH = Math.Max(2.0, Math.Round(s * 0.13));
        double textAreaH = s - barH - 1.0;
        double fontSize = pct >= 100 ? Math.Round(s * 0.62) : Math.Round(s * 0.78);

        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            SegoeUiBold,
            fontSize,
            CreateFrozenBrush(textColor),
            null,
            TextFormattingMode.Display,
            1.0);

        double maxW = s - 0.5;
        if (ft.Width > maxW && ft.Width > 0.1)
        {
            double sx = maxW / ft.Width;
            dc.PushTransform(new ScaleTransform(sx, 1.0, s / 2.0, textAreaH / 2.0));
            dc.DrawText(ft, new WpfPoint((s - ft.Width) / 2.0, Math.Round((textAreaH - ft.Height) / 2.0) - 0.5));
            dc.Pop();
        }
        else
        {
            dc.DrawText(ft, new WpfPoint(Math.Round((s - ft.Width) / 2.0), Math.Round((textAreaH - ft.Height) / 2.0) - 0.5));
        }

        double barY = s - barH;
        WpfColor trackColor = lightTaskbar ? WpfColor.FromArgb(65, 0, 0, 0) : WpfColor.FromArgb(65, 255, 255, 255);
        dc.DrawRectangle(CreateFrozenBrush(trackColor), null, new WpfRect(0, barY, s, barH));

        double fillW = Math.Max(2.0, Math.Round(s * (pct / 100.0)));
        dc.DrawRectangle(CreateFrozenBrush(accentBar), null, new WpfRect(0, barY, fillW, barH));
    }

    /// <summary>
    /// Pure 16x16..32x32 Tray Mode 2: Full-size crisp Fluent vector mouse silhouette filled by battery level.
    /// </summary>
    private static void DrawTrayBatteryFilledMouse(DrawingContext dc, double s, MouseRuntimeState state, bool lightTaskbar)
    {
        WpfColor stroke = ResolveInkColor(state, lightTaskbar, isPercentage: false);
        double mw = Math.Round(s * 0.64);
        double mh = Math.Round(s * 0.88);
        double mx = Math.Round((s - mw) / 2.0);
        double my = Math.Round((s - mh) / 2.0);
        double penW = Math.Max(1.25, s * 0.085);

        DrawVectorMouseGlyph(
            dc, mx, my, mw, mh, 1.0, stroke, penW,
            state.Status == MouseConnectionStatus.Disconnected ? null : state.BatteryPercent,
            state.IsCharging,
            fillInterior: true,
            lightTaskbar: lightTaskbar);

        if (state.IsCharging)
        {
            WpfColor boltCol = lightTaskbar ? WpfColor.FromRgb(22, 163, 74) : WpfColor.FromRgb(76, 195, 138);
            DrawVectorLightningBolt(dc, mx + mw - (s * 0.18), 0.0, s * 0.44, boltCol);
        }
    }

    /// <summary>
    /// Pure 16x16..32x32 Tray Mode 3: Crisp vector circular progress ring + DirectWrite centered digits.
    /// </summary>
    private static void DrawTrayRingBadge(DrawingContext dc, double s, MouseRuntimeState state, bool lightTaskbar)
    {
        bool disconnected = state.Status == MouseConnectionStatus.Disconnected || state.BatteryPercent is null;
        if (disconnected)
        {
            DrawTrayBatteryFilledMouse(dc, s, state, lightTaskbar);
            return;
        }

        int pct = Math.Clamp(state.BatteryPercent!.Value, 0, 100);
        double strokeW = Math.Max(1.6, s * 0.11);
        double radius = (s - strokeW) / 2.0 - 0.3;
        var center = new WpfPoint(s / 2.0, s / 2.0);

        WpfColor trackCol = lightTaskbar ? WpfColor.FromArgb(55, 0, 0, 0) : WpfColor.FromArgb(55, 255, 255, 255);
        dc.DrawEllipse(null, CreateFrozenPen(trackCol, strokeW), center, radius, radius);

        WpfColor arcCol = ResolveBarColor(state, lightTaskbar);
        double sweepDeg = Math.Clamp(360.0 * (pct / 100.0), 0.0, 359.5);
        if (sweepDeg >= 359.0)
        {
            dc.DrawEllipse(null, CreateFrozenPen(arcCol, strokeW), center, radius, radius);
        }
        else if (sweepDeg > 1.0)
        {
            var arcGeo = CreateArcGeometry(center, radius, -90.0, sweepDeg);
            var pen = new WpfPen(CreateFrozenBrush(arcCol), strokeW)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, arcGeo);
        }

        string text = pct >= 100 ? "99" : pct.ToString();
        double fontSize = Math.Max(7.0, Math.Round(s * 0.47));
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            SegoeUiBold,
            fontSize,
            CreateFrozenBrush(ResolveInkColor(state, lightTaskbar, isPercentage: true)),
            null,
            TextFormattingMode.Display,
            1.0);

        dc.DrawText(ft, new WpfPoint(Math.Round((s - ft.Width) / 2.0), Math.Round((s - ft.Height) / 2.0)));
    }

    private static void DrawVectorMouseGlyph(
        DrawingContext dc,
        double x,
        double y,
        double w,
        double h,
        double scale,
        WpfColor strokeColor,
        double strokeWidth,
        int? batteryPercent,
        bool isCharging,
        bool fillInterior,
        bool lightTaskbar)
    {
        double rx = w * 0.48;
        var bodyRect = new WpfRect(x, y, w, h);
        var bodyGeo = new RectangleGeometry(bodyRect, rx, rx);
        bodyGeo.Freeze();

        if (fillInterior && batteryPercent is int pct)
        {
            int clamped = Math.Clamp(pct, 0, 100);
            double fillH = h * (clamped / 100.0);
            WpfColor fillCol = isCharging
                ? WpfColor.FromArgb(165, 76, 195, 138)
                : clamped <= 15
                    ? WpfColor.FromArgb(190, 229, 72, 77)
                    : clamped <= 30
                        ? WpfColor.FromArgb(165, 217, 155, 67)
                        : lightTaskbar
                            ? WpfColor.FromArgb(120, 24, 24, 27)
                            : WpfColor.FromArgb(130, 237, 237, 237);

            dc.PushClip(bodyGeo);
            dc.DrawRectangle(CreateFrozenBrush(fillCol), null, new WpfRect(x, y + h - fillH, w, fillH));
            dc.Pop();
        }

        double halfStroke = strokeWidth / 2.0;
        var outlineRect = new WpfRect(
            x + halfStroke,
            y + halfStroke,
            Math.Max(1.0, w - strokeWidth),
            Math.Max(1.0, h - strokeWidth));
        double halfPx = 0.5 / scale;
        var gs = new GuidelineSet(
            [outlineRect.Left + halfPx, outlineRect.Right - halfPx],
            [outlineRect.Top + halfPx, outlineRect.Bottom - halfPx]);
        dc.PushGuidelineSet(gs);
        dc.DrawRoundedRectangle(null, CreateFrozenPen(strokeColor, strokeWidth), outlineRect, outlineRect.Width * 0.48, outlineRect.Width * 0.48);
        dc.Pop();

        // Crisp vertical scroll wheel capsule at top-center
        double wheelW = Math.Max(1.6, Math.Round(w * 0.20 * scale) / scale);
        double wheelH = Math.Max(3.0, Math.Round(h * 0.24 * scale) / scale);
        double wheelX = Math.Round((x + (w - wheelW) / 2.0) * scale) / scale;
        double wheelY = Math.Round((y + h * 0.20) * scale) / scale;
        dc.DrawRoundedRectangle(
            CreateFrozenBrush(strokeColor),
            null,
            new WpfRect(wheelX, wheelY, wheelW, wheelH),
            wheelW / 2.0,
            wheelW / 2.0);
    }

    private static void DrawVectorLightningBolt(DrawingContext dc, double x, double y, double size, WpfColor color)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new WpfPoint(x + size * 0.56, y), isFilled: true, isClosed: true);
            ctx.PolyLineTo(
            [
                new WpfPoint(x + size * 0.12, y + size * 0.54),
                new WpfPoint(x + size * 0.46, y + size * 0.54),
                new WpfPoint(x + size * 0.34, y + size * 1.00),
                new WpfPoint(x + size * 0.88, y + size * 0.42),
                new WpfPoint(x + size * 0.52, y + size * 0.42)
            ], isStroked: false, isSmoothJoin: false);
        }
        geo.Freeze();
        dc.DrawGeometry(CreateFrozenBrush(color), null, geo);
    }

    private static Geometry CreateArcGeometry(WpfPoint center, double radius, double startAngleDeg, double sweepAngleDeg)
    {
        double startRad = startAngleDeg * Math.PI / 180.0;
        double endRad = (startAngleDeg + sweepAngleDeg) * Math.PI / 180.0;

        var startPt = new WpfPoint(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        var endPt = new WpfPoint(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));
        bool isLargeArc = sweepAngleDeg > 180.0;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(startPt, isFilled: false, isClosed: false);
            ctx.ArcTo(endPt, new System.Windows.Size(radius, radius), 0.0, isLargeArc, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geo.Freeze();
        return geo;
    }

    private static DrawingVisual CreateCrispDrawingVisual()
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(visual, TextHintingMode.Fixed);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        return visual;
    }

    private static Bitmap RenderVisualToPremultipliedBitmap(
        DrawingVisual visual,
        int pixelW,
        int pixelH,
        double dpi,
        bool applyHitTestFloor,
        int hitInsetPx)
    {
        var rtb = new RenderTargetBitmap(pixelW, pixelH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);

        int stride = pixelW * 4;
        var pixels = new byte[stride * pixelH];
        rtb.CopyPixels(pixels, stride, 0);

        if (applyHitTestFloor)
        {
            int xMin = Math.Clamp(hitInsetPx, 0, pixelW - 1);
            int xMax = Math.Clamp(pixelW - hitInsetPx, xMin, pixelW);
            int yMin = Math.Clamp(hitInsetPx, 0, pixelH - 1);
            int yMax = Math.Clamp(pixelH - hitInsetPx, yMin, pixelH);

            for (int y = yMin; y < yMax; y++)
            {
                int row = y * stride;
                for (int x = xMin; x < xMax; x++)
                {
                    int idx = row + (x * 4);
                    if (pixels[idx + 3] == 0)
                    {
                        pixels[idx] = 0;
                        pixels[idx + 1] = 0;
                        pixels[idx + 2] = 0;
                        pixels[idx + 3] = 1;
                    }
                }
            }
        }

        var bmp = new Bitmap(pixelW, pixelH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, pixelW, pixelH), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            if (data.Stride == stride)
            {
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            else
            {
                for (int y = 0; y < pixelH; y++)
                {
                    Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    private static WpfColor ResolveInkColor(MouseRuntimeState state, bool lightTaskbar, bool isPercentage)
    {
        bool disconnected = state.Status == MouseConnectionStatus.Disconnected;
        bool sleeping = state.Status == MouseConnectionStatus.Sleeping;
        bool low = (state.BatteryPercent ?? 100) <= 15 && !state.IsCharging && !disconnected;

        if (low && isPercentage)
            return lightTaskbar ? WpfColor.FromRgb(220, 38, 38) : WpfColor.FromRgb(242, 109, 113);

        if (state.IsCharging && isPercentage)
            return lightTaskbar ? WpfColor.FromRgb(22, 163, 74) : WpfColor.FromRgb(112, 224, 168);

        byte alpha = disconnected ? (byte)130 : sleeping ? (byte)195 : (byte)255;
        return lightTaskbar
            ? WpfColor.FromArgb(alpha, 24, 24, 27)
            : WpfColor.FromArgb(alpha, 248, 248, 250);
    }

    private static WpfColor ResolveBarColor(MouseRuntimeState state, bool lightTaskbar)
    {
        if (state.IsCharging)
            return lightTaskbar ? WpfColor.FromRgb(22, 163, 74) : WpfColor.FromRgb(76, 195, 138);

        int pct = state.BatteryPercent ?? 100;
        if (pct <= 15)
            return lightTaskbar ? WpfColor.FromRgb(220, 38, 38) : WpfColor.FromRgb(229, 72, 77);
        if (pct <= 30)
            return lightTaskbar ? WpfColor.FromRgb(202, 138, 4) : WpfColor.FromRgb(217, 155, 67);

        return lightTaskbar ? WpfColor.FromRgb(22, 163, 74) : WpfColor.FromRgb(76, 195, 138);
    }

    private static string FormatCompactDeviceLabel(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "Mouse";
        var clean = rawName
            .Replace("DONGLE", "", StringComparison.OrdinalIgnoreCase)
            .Replace("2.4G", "", StringComparison.OrdinalIgnoreCase)
            .Replace("WIRELESS", "", StringComparison.OrdinalIgnoreCase)
            .Replace("RECEIVER", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GAMING MOUSE", "", StringComparison.OrdinalIgnoreCase)
            .Replace("MOUSE", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
        if (string.IsNullOrWhiteSpace(clean)) clean = "Mouse";
        return clean.Length > 10 ? clean[..10].Trim() : clean;
    }

    private static WpfBrush CreateFrozenBrush(WpfColor color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private static WpfPen CreateFrozenPen(WpfColor color, double thickness)
    {
        var p = new WpfPen(CreateFrozenBrush(color), thickness);
        p.Freeze();
        return p;
    }
}
