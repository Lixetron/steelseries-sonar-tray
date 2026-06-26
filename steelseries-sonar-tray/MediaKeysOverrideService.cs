using System.Runtime.InteropServices;

namespace SonarQuickMixer;

public sealed class MediaKeysOverrideService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;

    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;

    private const float VolumeStep = 0.02f;
    private const int ActionThrottleMs = 40;

    private readonly SonarApiClient _apiClient = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);

    private LowLevelKeyboardProc? _hookProc;
    private nint _hookHandle;
    private bool _enabled;
    private bool _disposed;
    private DateTime _lastActionUtc = DateTime.MinValue;
    private float _pendingVolumeDelta;
    private string _targetChannel = "master";

    public event Action? MixerChanged;
    public event Action<VolumeNotificationState>? VolumeAdjusted;

    public void SetTargetChannel(string channel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            _targetChannel = SonarChannels.NormalizeChannel(channel);
        }
    }

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            _pendingVolumeDelta = 0f;

            if (enabled)
            {
                InstallHook();
            }
            else
            {
                UninstallHook();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SetEnabled(false);
        _disposed = true;
        _actionGate.Dispose();
        _apiClient.Dispose();
    }

    private void InstallHook()
    {
        if (_hookHandle != 0)
        {
            return;
        }

        _hookProc = HookCallback;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
    }

    private void UninstallHook()
    {
        if (_hookHandle == 0)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = 0;
        _hookProc = null;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && IsEnabled())
        {
            var message = wParam.ToInt32();
            if (message is WmKeydown or WmSyskeydown)
            {
                var virtualKey = Marshal.ReadInt32(lParam);
                if (virtualKey is VkVolumeMute or VkVolumeDown or VkVolumeUp)
                {
                    QueueMediaKeyAction(virtualKey);
                    return 1;
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void QueueMediaKeyAction(int virtualKey)
    {
        if (virtualKey == VkVolumeUp)
        {
            AddPendingVolumeDelta(VolumeStep);
        }
        else if (virtualKey == VkVolumeDown)
        {
            AddPendingVolumeDelta(-VolumeStep);
        }
        else
        {
            _ = ProcessMediaKeyActionAsync(virtualKey);
        }
    }

    private void AddPendingVolumeDelta(float delta)
    {
        lock (_sync)
        {
            _pendingVolumeDelta += delta;
        }

        _ = ProcessMediaKeyActionAsync(0);
    }

    private async Task ProcessMediaKeyActionAsync(int virtualKey)
    {
        if (!IsEnabled())
        {
            return;
        }

        if (!await _actionGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (true)
            {
                var throttleDelay = GetThrottleDelay();
                if (throttleDelay > TimeSpan.Zero)
                {
                    await Task.Delay(throttleDelay).ConfigureAwait(false);
                }

                float volumeDelta;
                lock (_sync)
                {
                    volumeDelta = _pendingVolumeDelta;
                    _pendingVolumeDelta = 0f;
                }

                VolumeNotificationState? notification = null;
                var handled = false;
                if (Math.Abs(volumeDelta) > 0.0001f)
                {
                    notification = await AdjustTargetChannelVolumeAsync(volumeDelta).ConfigureAwait(false);
                    handled = notification.HasValue;
                }
                else if (virtualKey == VkVolumeMute)
                {
                    notification = await ToggleTargetChannelMuteAsync().ConfigureAwait(false);
                    handled = notification.HasValue;
                    virtualKey = 0;
                }

                if (handled)
                {
                    _lastActionUtc = DateTime.UtcNow;
                    MixerChanged?.Invoke();
                    if (notification.HasValue)
                    {
                        VolumeAdjusted?.Invoke(notification.Value);
                    }
                }

                lock (_sync)
                {
                    if (Math.Abs(_pendingVolumeDelta) <= 0.0001f)
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            // Media key override is best-effort.
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private TimeSpan GetThrottleDelay()
    {
        var elapsedMs = (DateTime.UtcNow - _lastActionUtc).TotalMilliseconds;
        var remainingMs = ActionThrottleMs - elapsedMs;
        return remainingMs > 0 ? TimeSpan.FromMilliseconds(remainingMs) : TimeSpan.Zero;
    }

    private async Task<VolumeNotificationState?> AdjustTargetChannelVolumeAsync(float delta)
    {
        if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
        {
            return null;
        }

        var channel = GetTargetChannel();
        var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
        if (!snapshot.Channels.TryGetValue(channel, out var channelSettings))
        {
            return null;
        }

        var currentVolume = channelSettings.Monitoring?.Volume ?? 0f;
        var isMuted = channelSettings.Monitoring?.Muted == true;
        var newVolume = Math.Clamp(currentVolume + delta, 0f, 1f);
        if (Math.Abs(newVolume - currentVolume) <= 0.0001f)
        {
            return null;
        }

        var updated = await _apiClient
            .SetVolumeAsync(channel, newVolume, SonarMixerPath.Monitoring)
            .ConfigureAwait(false);

        if (updated is null)
        {
            return null;
        }

        if (updated.TryGetValue(channel, out var updatedSettings))
        {
            var monitoring = updatedSettings.Monitoring;
            return new VolumeNotificationState(
                channel,
                monitoring?.Volume ?? newVolume,
                monitoring?.Muted == true);
        }

        return new VolumeNotificationState(channel, newVolume, isMuted);
    }

    private async Task<VolumeNotificationState?> ToggleTargetChannelMuteAsync()
    {
        if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
        {
            return null;
        }

        var channel = GetTargetChannel();
        var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
        if (!snapshot.Channels.TryGetValue(channel, out var channelSettings))
        {
            return null;
        }

        var muted = channelSettings.Monitoring?.Muted == true;
        var volume = channelSettings.Monitoring?.Volume ?? 0f;
        var updated = await _apiClient
            .SetMuteAsync(channel, !muted, SonarMixerPath.Monitoring)
            .ConfigureAwait(false);

        if (updated is null)
        {
            return null;
        }

        if (updated.TryGetValue(channel, out var updatedSettings))
        {
            var monitoring = updatedSettings.Monitoring;
            return new VolumeNotificationState(
                channel,
                monitoring?.Volume ?? volume,
                monitoring?.Muted == true);
        }

        return new VolumeNotificationState(channel, volume, !muted);
    }

    private string GetTargetChannel()
    {
        lock (_sync)
        {
            return _targetChannel;
        }
    }

    private bool IsEnabled()
    {
        lock (_sync)
        {
            return _enabled && !_disposed;
        }
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
