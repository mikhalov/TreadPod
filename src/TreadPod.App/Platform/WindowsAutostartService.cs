using Microsoft.Win32;
using TreadPod.Core.Bridge;

namespace TreadPod.App.Platform;

public sealed class WindowsAutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TreadPod";

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
