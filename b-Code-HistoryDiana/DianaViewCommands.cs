using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>Captures the running HistoryVulcan frontend for AI visual inspection.</summary>
internal static class DianaViewCommands
{
    private const string FrontendProcessName = "HistoryVulcan";

    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new CommandDescriptor
        {
            Name = "diana.view.windows",
            Domain = "HistoryDiana",
            CommandClass = "view",
            Summary = "列出可供 AI 图形查看器捕获的 HistoryVulcan 前端窗口",
            Example = "diana.view.windows",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => DianaCommandGuard.Run(() =>
            {
                var windows = DianaWindowCapture.ListWindows();
                return CommandResult.Ok($"找到 {windows.Count} 个可捕获的 HistoryVulcan 前端窗口", windows);
            })),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "diana.view.capture",
            Domain = "HistoryDiana",
            CommandClass = "view",
            Summary = "原尺寸捕获 HistoryVulcan 前端客户区为 PNG，供 AI 精确查看界面变化",
            Example = "diana.view.capture handle=0x123456",
            Readonly = false,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "handle",
                    Description = "diana.view.windows 返回的窗口句柄；省略时自动选择面积最大的可见前端窗口",
                    Required = false,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(context => DianaCommandGuard.Run(() =>
            {
                var handle = ParseHandle(context.GetString("handle"));
                var capture = DianaWindowCapture.Capture(handle);
                return CommandResult.Ok(
                    $"已捕获 {capture.Title}: {capture.Width}x{capture.Height} PNG，SHA-256 {capture.Sha256}",
                    capture);
            })),
        });
    }

    private static nint? ParseHandle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        var style = System.Globalization.NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = System.Globalization.NumberStyles.AllowHexSpecifier;
        }

        if (!long.TryParse(text, style, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new ArgumentException("handle 必须是 diana.view.windows 返回的十进制或 0x 十六进制窗口句柄");
        }

        return (nint)parsed;
    }

    internal static bool IsFrontendProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals(FrontendProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

internal static class DianaWindowCapture
{
    private const uint PwClientOnly = 0x00000001;
    private const uint PwRenderFullContent = 0x00000002;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    public static IReadOnlyList<WindowView> ListWindows()
    {
        var windows = new List<WindowView>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            var candidate = TryDescribe(handle);
            if (candidate is not null)
                windows.Add(candidate);
            return true;
        }, nint.Zero);

        return windows
            .OrderByDescending(window => window.ClientWidth * (long)window.ClientHeight)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static WindowCaptureResult Capture(nint? requestedHandle)
    {
        var windows = ListWindows();
        var target = requestedHandle.HasValue
            ? windows.SingleOrDefault(window => window.HandleValue == requestedHandle.Value.ToInt64())
              ?? throw new InvalidOperationException("指定窗口不是当前可捕获的 HistoryVulcan 前端窗口；请重新执行 diana.view.windows")
            : windows.FirstOrDefault()
              ?? throw new InvalidOperationException("当前没有可捕获的 HistoryVulcan 前端窗口");

        if (target.Minimized)
            throw new InvalidOperationException("目标前端窗口已最小化；恢复窗口后再捕获，避免得到空白画面");

        var captureRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HistoryVulcan", "HistoryDiana", "captures");
        Directory.CreateDirectory(captureRoot);
        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{target.ProcessId}-{target.HandleHex[2..]}-{Guid.NewGuid():N}.png";
        var path = Path.Combine(captureRoot, fileName);
        var rendered = CapturePixels((nint)target.HandleValue, target.ClientWidth, target.ClientHeight);
        SavePng(path, rendered.Pixels, target.ClientWidth, target.ClientHeight);

        var file = new FileInfo(path);
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var pixels = AnalyzePixels(rendered.Pixels);
        return new WindowCaptureResult(
            target.HandleHex,
            target.ProcessId,
            target.Title,
            path,
            target.ClientWidth,
            target.ClientHeight,
            file.Length,
            sha256,
            rendered.Method,
            pixels.SampledPixels,
            pixels.SampledUniqueColors,
            pixels.DarkPixelRatio,
            pixels.LightPixelRatio,
            pixels.NonUniform);
    }

    internal static void SavePng(string path, byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("像素缓冲区尺寸与图像尺寸不一致", nameof(pixels));

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            pixels,
            checked(width * 4));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
    }

    private static WindowView? TryDescribe(nint handle)
    {
        if (!NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle))
            return null;

        NativeMethods.GetWindowThreadProcessId(handle, out var processIdValue);
        var processId = checked((int)processIdValue);
        if (!DianaViewCommands.IsFrontendProcess(processId))
            return null;
        if (!NativeMethods.GetClientRect(handle, out var clientRect))
            return null;

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
            return null;

        var origin = new NativePoint();
        if (!NativeMethods.ClientToScreen(handle, ref origin))
            return null;

        return new WindowView(
            $"0x{handle.ToInt64():X}",
            handle.ToInt64(),
            processId,
            GetWindowTitle(handle),
            NativeMethods.IsIconic(handle),
            origin.X,
            origin.Y,
            width,
            height);
    }

    private static string GetWindowTitle(nint handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0)
            return "(无标题)";
        var title = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(handle, title, title.Capacity);
        return title.ToString();
    }

    private static RenderedPixels CapturePixels(nint window, int width, int height)
    {
        var screenDc = NativeMethods.GetDC(nint.Zero);
        if (screenDc == nint.Zero)
            throw new InvalidOperationException("无法取得桌面设备上下文");

        nint memoryDc = nint.Zero;
        nint bitmap = nint.Zero;
        nint previous = nint.Zero;
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero)
                throw new InvalidOperationException("无法创建图形查看器内存设备上下文");

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = checked((uint)(width * height * 4)),
                },
            };
            bitmap = NativeMethods.CreateDIBSection(screenDc, ref bitmapInfo, 0, out var bits, nint.Zero, 0);
            if (bitmap == nint.Zero || bits == nint.Zero)
                throw new InvalidOperationException("无法创建图形查看器像素缓冲区");
            previous = NativeMethods.SelectObject(memoryDc, bitmap);

            var method = "PrintWindow";
            if (!NativeMethods.PrintWindow(window, memoryDc, PwClientOnly | PwRenderFullContent))
            {
                var windowDc = NativeMethods.GetDC(window);
                if (windowDc == nint.Zero)
                    throw new InvalidOperationException("目标窗口拒绝图像捕获");
                try
                {
                    if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, SrcCopy | CaptureBlt))
                        throw new InvalidOperationException("目标窗口图像捕获失败");
                    method = "BitBlt";
                }
                finally
                {
                    _ = NativeMethods.ReleaseDC(window, windowDc);
                }
            }

            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new RenderedPixels(pixels, method);
        }
        finally
        {
            if (previous != nint.Zero && memoryDc != nint.Zero)
                _ = NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != nint.Zero)
                _ = NativeMethods.DeleteObject(bitmap);
            if (memoryDc != nint.Zero)
                _ = NativeMethods.DeleteDC(memoryDc);
            _ = NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    private static PixelAnalysis AnalyzePixels(byte[] pixels)
    {
        var step = Math.Max(1, pixels.Length / 4 / 16_384);
        var colors = new HashSet<int>();
        var sampled = 0;
        var dark = 0;
        var light = 0;
        for (var pixel = 0; pixel < pixels.Length / 4; pixel += step)
        {
            var offset = pixel * 4;
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            colors.Add((red << 16) | (green << 8) | blue);
            var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
            if (luminance <= 16)
                dark++;
            if (luminance >= 239)
                light++;
            sampled++;
        }

        return new PixelAnalysis(
            sampled,
            colors.Count,
            sampled == 0 ? 0 : Math.Round(dark / (double)sampled, 4),
            sampled == 0 ? 0 : Math.Round(light / (double)sampled, 4),
            colors.Count > 1);
    }

    private sealed record RenderedPixels(byte[] Pixels, string Method);
    private sealed record PixelAnalysis(
        int SampledPixels,
        int SampledUniqueColors,
        double DarkPixelRatio,
        double LightPixelRatio,
        bool NonUniform);
}

internal sealed record WindowView(
    string HandleHex,
    long HandleValue,
    int ProcessId,
    string Title,
    bool Minimized,
    int ClientX,
    int ClientY,
    int ClientWidth,
    int ClientHeight);

internal sealed record WindowCaptureResult(
    string Handle,
    int ProcessId,
    string Title,
    string Path,
    int Width,
    int Height,
    long Bytes,
    string Sha256,
    string CaptureMethod,
    int SampledPixels,
    int SampledUniqueColors,
    double DarkPixelRatio,
    double LightPixelRatio,
    bool NonUniform);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ColorsUsed;
    public uint ColorsImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfo
{
    public BitmapInfoHeader Header;
    public uint Colors;
}

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint handle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint handle, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint handle, StringBuilder title, int maximumCount);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint handle);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint handle, nint deviceContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(nint handle, nint deviceContext, uint flags);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint deviceContext, nint drawingObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint drawingObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        nint destination,
        int x,
        int y,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint operation);
}
