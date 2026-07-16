using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace DocVista.App;

public static class FileAssociationService
{
    private const string ProgId = "DocVista.Document";
    private static readonly string[] Extensions = [".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".csv"];

    public static void RegisterOpenWith()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        using var classes = Registry.CurrentUser.CreateSubKey("Software\\Classes");
        using (var progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue(null, "DocVista 只读文档");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{executable}\",0");
            using var command = progId.CreateSubKey("shell\\open\\command");
            command.SetValue(null, $"\"{executable}\" \"%1\"");
        }

        foreach (var extension in Extensions)
        {
            using var openWith = classes.CreateSubKey($"{extension}\\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        NotifyShell();
    }

    public static void UnregisterOpenWith()
    {
        using var classes = Registry.CurrentUser.CreateSubKey("Software\\Classes");
        foreach (var extension in Extensions)
        {
            using var openWith = classes.OpenSubKey($"{extension}\\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        NotifyShell();
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
