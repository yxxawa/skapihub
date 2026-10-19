using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Skapihub.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var profile = Environment.GetEnvironmentVariable("SKAPIHUB_DATA_DIR");
        var dataDirectory = string.IsNullOrWhiteSpace(profile)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "skapihub")
            : Path.GetFullPath(profile);
        var profileId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..16];
        var mutexName = string.IsNullOrWhiteSpace(profile) ? "Local\\Skapihub.Desktop" : "Local\\Skapihub." + profileId;
        var restoreMessage = NativeMethods.RegisterWindowMessage("Skapihub.Restore." + profileId);
        using var mutex = new Mutex(true, mutexName, out var first);
        if (!first)
        {
            NativeMethods.PostMessage(new IntPtr(0xffff), restoreMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        ApplicationConfiguration.Initialize();
        using var window = new MainWindow(dataDirectory, restoreMessage);
        Application.Run(window);
        mutex.ReleaseMutex();
        mutex.Dispose();
        if (window.RestartRequested) Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true });
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
