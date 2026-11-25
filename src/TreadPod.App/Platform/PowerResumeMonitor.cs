using Microsoft.Win32;

namespace TreadPod.App.Platform;

public sealed class PowerResumeMonitor : IDisposable
{
    private readonly Action _onResume;
    private bool _disposed;

    public PowerResumeMonitor(Action onResume)
    {
        _onResume = onResume;
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _onResume();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }
}
