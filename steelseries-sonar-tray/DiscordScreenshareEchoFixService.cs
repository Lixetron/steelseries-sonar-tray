using System.Diagnostics;
using NAudio.CoreAudioApi;
using SonarQuickMixer.Audio;

namespace SonarQuickMixer;

public sealed class DiscordScreenshareEchoFixService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SonarApiClient _apiClient = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, bool> _originalMuteStates = new(StringComparer.Ordinal);

    private System.Threading.Timer? _pollTimer;
    private bool _enabled;
    private bool _disposed;
    private bool _applyInProgress;

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

            if (enabled)
            {
                StartPolling();
                _ = RunApplyFixAsync();
            }
            else
            {
                StopPolling();
                _ = RunRestoreSavedMuteStatesAsync();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _enabled = false;
            StopPolling();
        }

        RestoreSavedMuteStates();
        _apiClient.Dispose();
        _disposed = true;
    }

    private void StartPolling()
    {
        _pollTimer ??= new System.Threading.Timer(
            _ => _ = RunApplyFixAsync(),
            null,
            PollInterval,
            PollInterval);
    }

    private void StopPolling()
    {
        if (_pollTimer is null)
        {
            return;
        }

        _pollTimer.Dispose();
        _pollTimer = null;
    }

    private async Task RunApplyFixAsync()
    {
        if (_applyInProgress)
        {
            return;
        }

        _applyInProgress = true;
        try
        {
            await ApplyFixAsync().ConfigureAwait(false);
        }
        finally
        {
            _applyInProgress = false;
        }
    }

    private async Task RunRestoreSavedMuteStatesAsync()
    {
        await Task.Run(RestoreSavedMuteStates).ConfigureAwait(false);
    }

    private async Task ApplyFixAsync()
    {
        if (!_enabled)
        {
            return;
        }

        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        var devices = new List<MMDevice>();

        try
        {
            SonarEchoFixRouting? routing = null;
            if (await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
            {
                routing = await _apiClient.GetEchoFixRoutingAsync().ConfigureAwait(false);
            }

            if (routing is not null)
            {
                CollectStreamerModeDevices(devices, routing);
                CollectClassicModeDevices(devices, routing);
            }
        }
        catch
        {
            // Sonar may be unavailable temporarily.
        }

        ApplyDevices(devices, activeKeys);
        PruneInactiveManagedSessions(activeKeys);
    }

    private static void CollectStreamerModeDevices(List<MMDevice> devices, SonarEchoFixRouting routing)
    {
        if (!routing.IsStreamerMode)
        {
            return;
        }

        TryAddDevice(devices, SonarVirtualMicrophoneRenderProbe.TryGetDevice());

        if (routing.IsMicrophoneStreamBroadcastEnabled)
        {
            TryAddDevice(devices, SonarVirtualStreamProbe.TryGetDevice());
        }

        if (routing.IsStreamMonitoringEnabled &&
            !string.IsNullOrWhiteSpace(routing.MonitoringOutputDeviceId))
        {
            var physicalDevice = WindowsAudioDeviceProbe.TryGetDevice(routing.MonitoringOutputDeviceId);
            if (physicalDevice is not null &&
                !WindowsAudioDeviceProbe.IsSonarVirtualDevice(physicalDevice.FriendlyName))
            {
                TryAddDevice(devices, physicalDevice);
            }
            else
            {
                physicalDevice?.Dispose();
            }
        }
    }

    private static void CollectClassicModeDevices(List<MMDevice> devices, SonarEchoFixRouting routing)
    {
        if (routing.IsStreamerMode)
        {
            return;
        }

        // Classic mode: mute Discord on Sonar Microphone render only.
        TryAddDevice(devices, SonarVirtualMicrophoneRenderProbe.TryGetDevice());
    }

    private static void TryAddDevice(List<MMDevice> devices, MMDevice? device)
    {
        if (device is null)
        {
            return;
        }

        var deviceKey = GetStaticDeviceKey(device);
        foreach (var existing in devices)
        {
            if (string.Equals(GetStaticDeviceKey(existing), deviceKey, StringComparison.Ordinal))
            {
                device.Dispose();
                return;
            }
        }

        devices.Add(device);
    }

    private static string GetStaticDeviceKey(MMDevice device)
    {
        if (SonarVirtualStreamProbe.IsSonarStreamDevice(device.FriendlyName))
        {
            return SonarVirtualStreamProbe.DeviceKey;
        }

        if (SonarVirtualMicrophoneRenderProbe.IsSonarVirtualMicrophone(device.FriendlyName))
        {
            return SonarVirtualMicrophoneRenderProbe.DeviceKey;
        }

        return device.ID;
    }

    private void ApplyDevices(List<MMDevice> devices, HashSet<string> activeKeys)
    {
        foreach (var device in devices)
        {
            var deviceKey = GetDeviceKey(device);
            try
            {
                device.AudioSessionManager.RefreshSessions();
                ApplyFixToSessions(deviceKey, device.AudioSessionManager.Sessions, activeKeys);
            }
            catch
            {
                // Device sessions may be unavailable during Sonar/Discord restarts.
            }
            finally
            {
                device.Dispose();
            }
        }
    }

    private void ApplyFixToSessions(
        string deviceId,
        SessionCollection sessions,
        ISet<string> activeKeys)
    {
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            try
            {
                if (!TryGetDiscordProcessId(session, out var processId))
                {
                    continue;
                }

                var sessionKey = BuildSessionKey(deviceId, session, processId);
                activeKeys.Add(sessionKey);
                MuteSession(sessionKey, session);
            }
            catch
            {
                // Session may disappear while we inspect it.
            }
        }
    }

    private void PruneInactiveManagedSessions(HashSet<string> activeKeys)
    {
        List<string> staleKeys;
        lock (_sync)
        {
            staleKeys = _originalMuteStates.Keys.Where(key => !activeKeys.Contains(key)).ToList();
        }

        if (staleKeys.Count == 0)
        {
            return;
        }

        RestoreSessionKeys(staleKeys);

        lock (_sync)
        {
            foreach (var staleKey in staleKeys)
            {
                _originalMuteStates.Remove(staleKey);
            }
        }
    }

    private void RestoreSessionKeys(IReadOnlyList<string> sessionKeys)
    {
        var statesToRestore = new Dictionary<string, bool>(StringComparer.Ordinal);
        lock (_sync)
        {
            foreach (var sessionKey in sessionKeys)
            {
                if (_originalMuteStates.TryGetValue(sessionKey, out var originalMute))
                {
                    statesToRestore[sessionKey] = originalMute;
                }
            }
        }

        if (statesToRestore.Count == 0)
        {
            return;
        }

        foreach (var deviceGroup in statesToRestore.Keys.GroupBy(GetDeviceIdFromSessionKey, StringComparer.Ordinal))
        {
            using var device = TryResolveDevice(deviceGroup.Key);
            if (device is null)
            {
                continue;
            }

            try
            {
                device.AudioSessionManager.RefreshSessions();
                RestoreSavedMuteStates(
                    deviceGroup.Key,
                    device.AudioSessionManager.Sessions,
                    statesToRestore);
            }
            catch
            {
                // Best-effort restore when Sonar/Discord routing changes.
            }
        }
    }

    private void MuteSession(string sessionKey, AudioSessionControl session)
    {
        try
        {
            var volume = session.SimpleAudioVolume;

            lock (_sync)
            {
                if (!_originalMuteStates.ContainsKey(sessionKey))
                {
                    _originalMuteStates[sessionKey] = volume.Mute;
                }
            }

            if (!volume.Mute)
            {
                volume.Mute = true;
            }
        }
        catch
        {
            // Session may disappear while we mute it.
        }
    }

    private void RestoreSavedMuteStates()
    {
        Dictionary<string, bool> statesToRestore;
        lock (_sync)
        {
            if (_originalMuteStates.Count == 0)
            {
                return;
            }

            statesToRestore = new Dictionary<string, bool>(_originalMuteStates, StringComparer.Ordinal);
            _originalMuteStates.Clear();
        }

        foreach (var deviceGroup in statesToRestore.Keys.GroupBy(GetDeviceIdFromSessionKey, StringComparer.Ordinal))
        {
            using var device = TryResolveDevice(deviceGroup.Key);
            if (device is null)
            {
                continue;
            }

            try
            {
                device.AudioSessionManager.RefreshSessions();
                RestoreSavedMuteStates(
                    deviceGroup.Key,
                    device.AudioSessionManager.Sessions,
                    statesToRestore);
            }
            catch
            {
                // Best-effort restore on disable/exit.
            }
        }
    }

    private static void RestoreSavedMuteStates(
        string deviceId,
        SessionCollection sessions,
        IReadOnlyDictionary<string, bool> statesToRestore)
    {
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            try
            {
                if (!TryGetDiscordProcessId(session, out var processId))
                {
                    continue;
                }

                var sessionKey = BuildSessionKey(deviceId, session, processId);
                if (!statesToRestore.TryGetValue(sessionKey, out var originalMute))
                {
                    continue;
                }

                session.SimpleAudioVolume.Mute = originalMute;
            }
            catch
            {
                // Session may disappear while we restore it.
            }
        }
    }

    private static string GetDeviceKey(MMDevice device) => GetStaticDeviceKey(device);

    private static MMDevice? TryResolveDevice(string deviceId)
    {
        if (SonarVirtualStreamProbe.IsSonarStreamDeviceId(deviceId))
        {
            return SonarVirtualStreamProbe.TryGetDevice();
        }

        if (SonarVirtualMicrophoneRenderProbe.IsSonarMicrophoneRenderDeviceId(deviceId))
        {
            return SonarVirtualMicrophoneRenderProbe.TryGetDevice();
        }

        return WindowsAudioDeviceProbe.TryGetDevice(deviceId);
    }

    private static string GetDeviceIdFromSessionKey(string sessionKey)
    {
        var separatorIndex = sessionKey.IndexOf('|', StringComparison.Ordinal);
        return separatorIndex < 0 ? sessionKey : sessionKey[..separatorIndex];
    }

    private static bool TryGetDiscordProcessId(AudioSessionControl session, out uint processId)
    {
        processId = 0;

        try
        {
            processId = session.GetProcessID;
        }
        catch
        {
            return false;
        }

        if (processId == 0)
        {
            return false;
        }

        return IsDiscordProcess(processId);
    }

    private static bool IsDiscordProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildSessionKey(string deviceId, AudioSessionControl session, uint processId)
    {
        try
        {
            return $"{deviceId}|{processId}:{session.GetSessionIdentifier}";
        }
        catch
        {
            try
            {
                return $"{deviceId}|{processId}:{session.DisplayName}";
            }
            catch
            {
                return $"{deviceId}|{processId}";
            }
        }
    }
}
