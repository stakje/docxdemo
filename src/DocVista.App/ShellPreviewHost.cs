using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Input;
using System.Windows.Interop;

namespace DocVista.App;

[SupportedOSPlatform("windows")]
public sealed class ShellPreviewHost : HwndHost
{
    private const string PreviewHandlerKey = "{8895b1c6-B41F-4C1C-A562-0D564250836F}";
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private IntPtr _hostWindow;
    private object? _previewObject;
    private IPreviewHandler? _previewHandler;
    private PreviewRect _lastRect;

    public ShellPreviewHost()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostWindow = CreateWindowEx(0, "static", string.Empty, WsChild | WsVisible, 0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hostWindow == IntPtr.Zero) throw new InvalidOperationException("无法创建文档预览窗口");
        return new HandleRef(this, _hostWindow);
    }

    public void LoadPreview(string path)
    {
        UnloadPreview();
        var handlerId = FindPreviewHandler(Path.GetExtension(path));
        if (handlerId is null) throw new NotSupportedException("系统中没有为此格式注册预览组件。可安装 Microsoft Office 或 LibreOffice 后重试。");

        var type = Type.GetTypeFromCLSID(handlerId.Value, throwOnError: true)!;
        _previewObject = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法启动系统预览组件");
        if (_previewObject is not IInitializeWithFile initializer || _previewObject is not IPreviewHandler handler)
            throw new NotSupportedException("已注册的预览组件不支持安全文件初始化");

        Marshal.ThrowExceptionForHR(initializer.Initialize(path, 0));
        _previewHandler = handler;
        var rect = CurrentRect();
        Marshal.ThrowExceptionForHR(handler.SetWindow(_hostWindow, ref rect));
        _lastRect = rect;
        Marshal.ThrowExceptionForHR(handler.DoPreview());
    }

    public bool TryAdjustZoom(int direction, int steps = 1)
    {
        if (_previewHandler is null || direction == 0 || steps <= 0) return false;
        try
        {
            _previewHandler.SetFocus();
            _previewHandler.QueryFocus(out var focusedWindow);
            var target = focusedWindow == IntPtr.Zero ? _hostWindow : focusedWindow;
            var delta = direction > 0 ? 120 : -120;
            var wheelParameters = new IntPtr((delta << 16) | 0x0008);
            for (var index = 0; index < steps; index++)
                if (SendMessageTimeout(target, 0x020A, wheelParameters, IntPtr.Zero, 0x0002, 200, out _) == IntPtr.Zero) return false;
            return true;
        }
        catch { return false; }
    }

    protected override bool TabIntoCore(TraversalRequest request) => TryFocusPreview();

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        TryFocusPreview();
    }

    protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers)
    {
        if (_previewHandler is null) return false;
        try
        {
            var previewMessage = new PreviewMessage
            {
                Window = msg.hwnd,
                Message = unchecked((uint)msg.message),
                WParam = msg.wParam,
                LParam = msg.lParam,
                Time = unchecked((uint)msg.time),
                PointX = msg.pt_x,
                PointY = msg.pt_y
            };
            return _previewHandler.TranslateAccelerator(ref previewMessage) == 0;
        }
        catch { return false; }
    }

    private bool TryFocusPreview()
    {
        if (_previewHandler is null) return false;
        try
        {
            Marshal.ThrowExceptionForHR(_previewHandler.SetFocus());
            return true;
        }
        catch { return false; }
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_previewHandler is null) return;
        var rect = CurrentRect();
        if (SameRect(rect, _lastRect)) return;
        try
        {
            Marshal.ThrowExceptionForHR(_previewHandler.SetRect(ref rect));
            _lastRect = rect;
        }
        catch (Exception exception) { Debug.WriteLine($"系统预览组件调整尺寸失败：{exception}"); }
    }

    public void UnloadPreview()
    {
        try { _previewHandler?.Unload(); } catch { }
        _previewHandler = null;
        _lastRect = default;
        if (_previewObject is not null && Marshal.IsComObject(_previewObject)) Marshal.FinalReleaseComObject(_previewObject);
        _previewObject = null;
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        UnloadPreview();
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        _hostWindow = IntPtr.Zero;
    }

    private PreviewRect CurrentRect()
    {
        GetClientRect(_hostWindow, out var rect);
        return rect;
    }

    private static bool SameRect(PreviewRect left, PreviewRect right) =>
        left.Left == right.Left && left.Top == right.Top && left.Right == right.Right && left.Bottom == right.Bottom;

    private static Guid? FindPreviewHandler(string extension)
    {
        var paths = new List<string> { $"{extension}\\shellex\\{PreviewHandlerKey}", $"SystemFileAssociations\\{extension}\\shellex\\{PreviewHandlerKey}" };
        using (var extensionKey = Registry.ClassesRoot.OpenSubKey(extension))
        {
            if (extensionKey?.GetValue(null) is string progId && !string.IsNullOrWhiteSpace(progId)) paths.Insert(1, $"{progId}\\shellex\\{PreviewHandlerKey}");
        }

        foreach (var path in paths)
        {
            using var key = Registry.ClassesRoot.OpenSubKey(path);
            if (key?.GetValue(null) is string value && Guid.TryParse(value, out var id)) return id;
        }
        return null;
    }

    [ComImport, Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
    }

    [ComImport, Guid("8895B1C6-B41F-4C1C-A562-0D564250836F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        [PreserveSig] int SetWindow(IntPtr parent, ref PreviewRect rect);
        [PreserveSig] int SetRect(ref PreviewRect rect);
        [PreserveSig] int DoPreview();
        [PreserveSig] int Unload();
        [PreserveSig] int SetFocus();
        [PreserveSig] int QueryFocus(out IntPtr hwnd);
        [PreserveSig] int TranslateAccelerator(ref PreviewMessage message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out PreviewRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
