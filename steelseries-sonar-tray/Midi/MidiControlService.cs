using SonarQuickMixer.Services;
using SonarQuickMixer.Settings;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Orchestrates MIDI input → Sonar volume/mute, with absolute/relative parsing and fader priority.
/// </summary>
public sealed class MidiControlService : IDisposable
{
    private const int SnapshotPollMs = 500;
    private const int MidiActiveWindowMs = 750;
    /// <summary>
    /// Cap Sonar Volume PUT rate to roughly match GG Sonar UI (~80–100 ms between writes).
    /// Faster floods make Sonar 118+ OSD/UI jerk; our sliders stay smooth via optimistic VolumeAdjusted.
    /// </summary>
    private const int SonarWriteMinIntervalMs = 90;

    private readonly AppSettings _settings;
    private readonly MidiMappingStore _mappingStore;
    private readonly MidiControlStateStore _controlStateStore;
    private readonly MidiInputHub _hub;
    private readonly MidiOutputHub _outputHub;
    private readonly PresetCatalog _presets;
    private readonly FaderPriorityGuard _faderGuard;
    private readonly SonarApiClient _apiClient = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly Dictionary<string, float> _volumeCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last volume successfully written to Sonar per channel|path — skip redundant PUTs.</summary>
    private readonly Dictionary<string, float> _lastSonarWrittenVolumes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last known mute state per channel key for LED feedback diffing.</summary>
    private readonly Dictionary<string, bool> _muteCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _channelAssignedLedCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last physical fader position (0..1) keyed by deviceCore|stripIndex — for match-LED extinguish.</summary>
    private readonly Dictionary<string, float> _lastFaderHardware = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last Pitch Bend MSB we sent for soft-takeover, keyed by deviceCore|controlId.</summary>
    private readonly Dictionary<string, int> _lastFaderLedMsbSent = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Unsaved LED feedback edits from MIDI Setup. Value null = Off (suppress disk preset until Save/Discard).
    /// Key: deviceCore|controlId.
    /// </summary>
    private readonly Dictionary<string, MidiControlFeedbackSpec?> _feedbackStage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _feedbackBlinkTimer;
    private bool _feedbackBlinkPhase;
    /// <summary>
    /// Latest absolute fader positions waiting to be written to Sonar while an HTTP call is in flight.
    /// Without this, rapid sweeps drop the final 0%/100% event and leave Sonar near the extreme.
    /// </summary>
    private readonly Dictionary<string, (MidiBinding Binding, int RawValue)> _pendingAbsoluteVolumes =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Accumulated relative encoder ticks per channel. Must sum (not replace) — dropping ticks
    /// makes fast spins require many extra turns to cover 0→100%.
    /// </summary>
    private readonly Dictionary<string, (MidiBinding Binding, int Ticks)> _pendingRelativeTicks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _snapshotTimer;

    private bool _enabled;
    private bool _disposed;
    private bool _absoluteRestoreRunning;
    private DateTime _lastActionUtc = DateTime.MinValue;
    private DateTime _lastSonarWriteUtc = DateTime.MinValue;

    // MIDI Learn
    private string? _learnDeviceName;
    private string? _learnControlId;
    private MidiValueMode _learnDefaultMode = MidiValueMode.Absolute;
    private TaskCompletionSource<MidiIncomingEvent>? _learnCompletion;

    public MidiControlService(
        AppSettings settings,
        MidiMappingStore? mappingStore = null,
        MidiInputHub? hub = null,
        FaderPriorityGuard? faderGuard = null,
        MidiControlStateStore? controlStateStore = null,
        PresetCatalog? presets = null,
        MidiOutputHub? outputHub = null)
    {
        _settings = settings;
        _mappingStore = mappingStore ?? new MidiMappingStore();
        _controlStateStore = controlStateStore ?? new MidiControlStateStore();
        _hub = hub ?? new MidiInputHub();
        _outputHub = outputHub ?? new MidiOutputHub();
        _presets = presets ?? new PresetCatalog();
        _faderGuard = faderGuard ?? new FaderPriorityGuard();

        _hub.EventReceived += OnMidiEvent;
        _faderGuard.RollbackRequested += OnRollbackRequested;
        _snapshotTimer = new System.Threading.Timer(
            _ => _ = PollSnapshotAsync(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        _feedbackBlinkTimer = new System.Threading.Timer(
            _ => TickFeedbackBlink(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public MidiMappingStore MappingStore => _mappingStore;

    public MidiControlStateStore ControlStateStore => _controlStateStore;

    public MidiInputHub Hub => _hub;

    public MidiOutputHub OutputHub => _outputHub;

    public PresetCatalog Presets => _presets;

    public FaderPriorityGuard FaderGuard => _faderGuard;

    public event Action? MixerChanged;
    public event Action<VolumeNotificationState>? VolumeAdjusted;
    public event Action<MidiControlFeedback>? ControlFeedback;
    public event Action<MidiIncomingEvent>? RawEventReceived;

    /// <summary>
    /// True when a MIDI volume/mute write happened recently or pending drain is queued.
    /// Used to avoid stale Sonar GET overwriting UI / fader-guard hardware memory.
    /// </summary>
    public bool WasRecentlyActive(TimeSpan? window = null)
    {
        var windowMs = window?.TotalMilliseconds ?? MidiActiveWindowMs;
        lock (_sync)
        {
            if (_pendingAbsoluteVolumes.Count > 0 || _pendingRelativeTicks.Count > 0)
            {
                return true;
            }

            return (DateTime.UtcNow - _lastActionUtc).TotalMilliseconds < windowMs;
        }
    }

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            // Idempotent: re-applying the same state (e.g. other Settings toggles) must not
            // re-run RestoreAbsolutePositionsAsync — Sonar 118+ treats those Volume PUTs as OSD events.
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
        }

        _hub.SetEnabledDevices(_mappingStore.EnabledDevices);
        _outputHub.SetEnabledDevices(_mappingStore.EnabledDevices);
        // Keep devices open whenever the user selected any — needed for Learn / Blueprint feedback.
        var hasDevices = _mappingStore.EnabledDevices.Count > 0;
        _hub.SetListening(hasDevices);
        _outputHub.SetListening(hasDevices);

        if (enabled)
        {
            _snapshotTimer.Change(SnapshotPollMs, SnapshotPollMs);
            _feedbackBlinkTimer.Change(400, 400);
            _ = RestoreAbsolutePositionsAsync();
            _ = RefreshHardwareFeedbackAsync(force: true);
        }
        else
        {
            _snapshotTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _feedbackBlinkTimer.Change(Timeout.Infinite, Timeout.Infinite);
            CancelLearn();
        }
    }

    public void ApplyEnabledDevicesFromStore()
    {
        var available = _hub.GetAvailableDeviceNames();
        _mappingStore.MigrateSecondaryPortBindings(available);
        _hub.SetEnabledDevices(_mappingStore.EnabledDevices);
        _outputHub.SetEnabledDevices(_mappingStore.EnabledDevices);
        var hasDevices = _mappingStore.EnabledDevices.Count > 0;
        _hub.SetListening(hasDevices);
        _outputHub.SetListening(hasDevices);
        if (_enabled)
        {
            _ = RefreshHardwareFeedbackAsync(force: true);
        }
    }

    public void ReloadMappings() => _mappingStore.Load();

    /// <summary>
    /// Waits for the next CC/note. When <paramref name="deviceName"/> is null, accepts any enabled device.
    /// </summary>
    public Task<MidiIncomingEvent> BeginLearnAsync(
        string? deviceName,
        string? controlId,
        MidiValueMode defaultMode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancelLearn();
        var tcs = new TaskCompletionSource<MidiIncomingEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _learnDeviceName = deviceName;
            _learnControlId = controlId;
            _learnDefaultMode = defaultMode;
            _learnCompletion = tcs;
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                CancelLearn();
                tcs.TrySetCanceled(cancellationToken);
            });
        }

        // Ensure ports are open even if Sonar routing is disabled.
        ApplyEnabledDevicesFromStore();

        return tcs.Task;
    }

    public void CancelLearn()
    {
        TaskCompletionSource<MidiIncomingEvent>? tcs;
        lock (_sync)
        {
            tcs = _learnCompletion;
            _learnCompletion = null;
            _learnDeviceName = null;
            _learnControlId = null;
        }

        tcs?.TrySetCanceled();
    }

    public bool IsLearning
    {
        get
        {
            lock (_sync)
            {
                return _learnCompletion is not null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelLearn();
        _snapshotTimer.Dispose();
        _feedbackBlinkTimer.Dispose();
        _hub.EventReceived -= OnMidiEvent;
        _faderGuard.RollbackRequested -= OnRollbackRequested;
        _hub.Dispose();
        _outputHub.Dispose();
        _faderGuard.Dispose();
        _controlStateStore.Dispose();
        _actionGate.Dispose();
        _apiClient.Dispose();
    }

    private void OnMidiEvent(MidiIncomingEvent evt)
    {
        RawEventReceived?.Invoke(evt);

        if (evt.IsPitchBend)
        {
            RememberFaderHardware(evt.DeviceName, evt.Controller, MidiValueParser.PitchBendToVolume(evt.RawValue));
        }

        ControlFeedback?.Invoke(new MidiControlFeedback(
            evt.DeviceName,
            evt.Controller,
            evt.RawValue,
            MidiValueParser.ToNormalizedVolume(evt.IsPitchBend, evt.RawValue),
            evt.IsNote,
            evt.IsPitchBend));

        if (TryCompleteLearn(evt))
        {
            return;
        }

        if (!IsEnabled())
        {
            return;
        }

        var binding = _mappingStore.FindByController(
            evt.DeviceName,
            evt.Controller,
            evt.IsNote,
            evt.IsPitchBend);
        if (binding is null || !binding.HasSonarChannel)
        {
            // Unmapped / unbound Pitch Bend: echo position so match LED can extinguish.
            if (evt.IsPitchBend)
            {
                EchoFaderMatchLed(evt.DeviceName, evt.Controller, MidiValueParser.PitchBendToVolume(evt.RawValue));
            }

            return;
        }

        if (binding.IsNote && !evt.IsNoteOn)
        {
            return;
        }

        _ = ProcessBindingAsync(binding, evt);
    }

    private bool TryCompleteLearn(MidiIncomingEvent evt)
    {
        TaskCompletionSource<MidiIncomingEvent>? tcs;
        string? deviceFilter;
        lock (_sync)
        {
            tcs = _learnCompletion;
            deviceFilter = _learnDeviceName;
            if (tcs is null)
            {
                return false;
            }

            if (!IsLearnEventAccepted(evt.DeviceName, deviceFilter))
            {
                return true; // swallow while learning
            }

            // Prefer CC events for learn unless only notes arrive.
            if (!evt.IsNote || evt.IsNoteOn)
            {
                _learnCompletion = null;
                _learnDeviceName = null;
            }
            else
            {
                return true;
            }
        }

        if (!evt.IsNote || evt.IsNoteOn)
        {
            tcs.TrySetResult(evt);
            return true;
        }

        return true;
    }

    private bool IsLearnEventAccepted(string deviceName, string? deviceFilter)
    {
        if (!string.IsNullOrWhiteSpace(deviceFilter))
        {
            return string.Equals(deviceName, deviceFilter, StringComparison.OrdinalIgnoreCase);
        }

        var enabled = _mappingStore.EnabledDevices;
        if (enabled.Count == 0)
        {
            return true;
        }

        return enabled.Any(d => string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ProcessBindingAsync(MidiBinding binding, MidiIncomingEvent evt)
    {
        if (IsAbsoluteVolumeBinding(binding))
        {
            EnqueueAbsoluteVolume(binding, evt.RawValue);
            await DrainPendingVolumesAsync().ConfigureAwait(false);
            return;
        }

        if (IsRelativeVolumeBinding(binding))
        {
            EnqueueRelativeTicks(binding, evt.RawValue);
            await DrainPendingVolumesAsync().ConfigureAwait(false);
            return;
        }

        if (!await _actionGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            VolumeNotificationState? notification = null;

            if (binding.Action == MidiBindingAction.MuteToggle)
            {
                notification = await ToggleMuteAsync(binding).ConfigureAwait(false);
            }

            if (notification.HasValue)
            {
                _lastActionUtc = DateTime.UtcNow;
                MixerChanged?.Invoke();
                VolumeAdjusted?.Invoke(notification.Value);
            }
        }
        catch
        {
            // MIDI control is best-effort.
        }
        finally
        {
            _actionGate.Release();
            // Volume events may have queued while mute held the gate.
            _ = DrainPendingVolumesAsync();
        }
    }

    private static bool IsAbsoluteVolumeBinding(MidiBinding binding) =>
        binding.Mode == MidiValueMode.Absolute
        && binding.Action == MidiBindingAction.Volume
        && !binding.IsNote;

    private static bool IsRelativeVolumeBinding(MidiBinding binding) =>
        binding.Mode == MidiValueMode.Relative
        && binding.Action == MidiBindingAction.Volume
        && !binding.IsNote;

    private void EnqueueAbsoluteVolume(MidiBinding binding, int rawValue)
    {
        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        var preview = MidiValueParser.ToNormalizedVolume(binding.IsPitchBend, rawValue);
        _faderGuard.CancelRollbackForBinding(binding);
        _faderGuard.RememberHardwareVolume(binding, preview);
        CacheVolume(binding, preview);

        lock (_sync)
        {
            // Keep only the newest hardware position per Sonar channel/path.
            _pendingAbsoluteVolumes[key] = (binding, rawValue);
            _lastActionUtc = DateTime.UtcNow;
        }

        // Optimistic UI at MIDI event rate; Sonar catches up via coalesced live PUTs.
        VolumeAdjusted?.Invoke(BuildLiveNotification(binding, preview));
    }

    private void EnqueueRelativeTicks(MidiBinding binding, int rawValue)
    {
        var ticks = MidiValueParser.ParseRelativeTicks(rawValue, binding.RelativeEncoding);
        if (ticks == 0)
        {
            return;
        }

        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        lock (_sync)
        {
            if (_pendingRelativeTicks.TryGetValue(key, out var existing))
            {
                _pendingRelativeTicks[key] = (binding, existing.Ticks + ticks);
            }
            else
            {
                _pendingRelativeTicks[key] = (binding, ticks);
            }
        }
    }

    private async Task DrainPendingVolumesAsync()
    {
        if (!await _actionGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (true)
            {
                List<(MidiBinding Binding, int RawValue)> absoluteBatch;
                List<(MidiBinding Binding, int Ticks)> relativeBatch;
                lock (_sync)
                {
                    if (_pendingAbsoluteVolumes.Count == 0 && _pendingRelativeTicks.Count == 0)
                    {
                        break;
                    }

                    absoluteBatch = _pendingAbsoluteVolumes.Values.ToList();
                    relativeBatch = _pendingRelativeTicks.Values.ToList();
                    _pendingAbsoluteVolumes.Clear();
                    _pendingRelativeTicks.Clear();
                }

                var pace = GetSonarWriteDelay();
                if (pace > TimeSpan.Zero)
                {
                    await Task.Delay(pace).ConfigureAwait(false);
                }

                // After pacing, fold in anything that arrived — always send the freshest position.
                lock (_sync)
                {
                    if (_pendingAbsoluteVolumes.Count > 0)
                    {
                        var merged = new Dictionary<string, (MidiBinding Binding, int RawValue)>(
                            StringComparer.OrdinalIgnoreCase);
                        foreach (var item in absoluteBatch)
                        {
                            merged[FaderPriorityGuard.ChannelKey(item.Binding.ChannelId, item.Binding.Path)] = item;
                        }

                        foreach (var kv in _pendingAbsoluteVolumes)
                        {
                            merged[kv.Key] = kv.Value;
                        }

                        _pendingAbsoluteVolumes.Clear();
                        absoluteBatch = merged.Values.ToList();
                    }

                    if (_pendingRelativeTicks.Count > 0)
                    {
                        var merged = new Dictionary<string, (MidiBinding Binding, int Ticks)>(
                            StringComparer.OrdinalIgnoreCase);
                        foreach (var item in relativeBatch)
                        {
                            merged[FaderPriorityGuard.ChannelKey(item.Binding.ChannelId, item.Binding.Path)] = item;
                        }

                        foreach (var kv in _pendingRelativeTicks)
                        {
                            if (merged.TryGetValue(kv.Key, out var existing))
                            {
                                merged[kv.Key] = (kv.Value.Binding, existing.Ticks + kv.Value.Ticks);
                            }
                            else
                            {
                                merged[kv.Key] = kv.Value;
                            }
                        }

                        _pendingRelativeTicks.Clear();
                        relativeBatch = merged.Values.ToList();
                    }
                }

                var wroteSonar = false;
                foreach (var (pendingBinding, rawValue) in absoluteBatch)
                {
                    try
                    {
                        var notification = await ApplyAbsoluteAsync(pendingBinding, rawValue).ConfigureAwait(false);
                        if (notification.HasValue)
                        {
                            wroteSonar = true;
                            _lastActionUtc = DateTime.UtcNow;
                            // VolumeAdjusted already fired optimistically on enqueue; keep a confirm tick.
                            VolumeAdjusted?.Invoke(notification.Value);
                        }
                    }
                    catch
                    {
                        // MIDI control is best-effort.
                    }
                }

                foreach (var (pendingBinding, ticks) in relativeBatch)
                {
                    try
                    {
                        var notification = await ApplyRelativeTicksAsync(pendingBinding, ticks).ConfigureAwait(false);
                        if (notification.HasValue)
                        {
                            wroteSonar = true;
                            _lastActionUtc = DateTime.UtcNow;
                            VolumeAdjusted?.Invoke(notification.Value);
                        }
                    }
                    catch
                    {
                        // MIDI control is best-effort.
                    }
                }

                if (wroteSonar)
                {
                    _lastSonarWriteUtc = DateTime.UtcNow;
                }
            }
        }
        finally
        {
            _actionGate.Release();
        }

        // Race: an event may have enqueued after the empty-check but before Release.
        bool hasPending;
        lock (_sync)
        {
            hasPending = _pendingAbsoluteVolumes.Count > 0 || _pendingRelativeTicks.Count > 0;
        }

        if (hasPending)
        {
            await DrainPendingVolumesAsync().ConfigureAwait(false);
        }
    }

    private async Task<VolumeNotificationState?> ApplyAbsoluteAsync(MidiBinding binding, int rawValue)
    {
        var volume = MidiValueParser.ToNormalizedVolume(binding.IsPitchBend, rawValue);
        _faderGuard.CancelRollbackForBinding(binding);
        _faderGuard.RememberHardwareVolume(binding, volume);

        // Same as last successful Sonar write (GG also re-PUTs identical values — we skip).
        if (IsSameAsLastSonarWrite(binding, volume))
        {
            CacheVolume(binding, volume);
            _controlStateStore.SetFromBinding(binding, volume);
            RefreshFaderMatchLedAfterHardwareMove(binding);
            return null;
        }

        _faderGuard.MarkMidiOriginated(binding.ChannelId, binding.Path);

        if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
        {
            return null;
        }

        // Live path: skip response JSON parse so each paced sample stays cheap.
        if (!await _apiClient
                .SetVolumeLiveAsync(binding.ChannelId, volume, binding.Path)
                .ConfigureAwait(false))
        {
            return null;
        }

        RememberSonarWrite(binding, volume);
        CacheVolume(binding, volume);
        _controlStateStore.SetFromBinding(binding, volume);
        RefreshFaderMatchLedAfterHardwareMove(binding);
        return BuildLiveNotification(binding, volume);
    }

    private async Task<VolumeNotificationState?> ApplyRelativeTicksAsync(MidiBinding binding, int ticks)
    {
        if (ticks == 0)
        {
            return null;
        }

        var step = binding.RelativeStep ?? _settings.MidiRelativeStep;
        if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
        {
            return null;
        }

        var current = await GetCurrentVolumeAsync(binding).ConfigureAwait(false);
        var newVolume = MidiValueParser.ApplyRelativeDelta(current, ticks, step);
        if (Math.Abs(newVolume - current) <= FaderPriorityGuard.VolumeEpsilon
            || IsSameAsLastSonarWrite(binding, newVolume))
        {
            return null;
        }

        _faderGuard.MarkMidiOriginated(binding.ChannelId, binding.Path);
        if (!await _apiClient
                .SetVolumeLiveAsync(binding.ChannelId, newVolume, binding.Path)
                .ConfigureAwait(false))
        {
            return null;
        }

        RememberSonarWrite(binding, newVolume);
        CacheVolume(binding, newVolume);
        return BuildLiveNotification(binding, newVolume);
    }

    private async Task<VolumeNotificationState?> ToggleMuteAsync(MidiBinding binding)
    {
        if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
        {
            return null;
        }

        var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
        if (!snapshot.Channels.TryGetValue(SonarChannels.NormalizeChannel(binding.ChannelId), out var settings))
        {
            return null;
        }

        var state = binding.Path == SonarMixerPath.Streaming ? settings.Streaming : settings.Monitoring;
        var muted = state?.Muted == true;
        var volume = state?.Volume ?? 0f;

        var updated = await _apiClient
            .SetMuteAsync(binding.ChannelId, !muted, binding.Path)
            .ConfigureAwait(false);

        if (updated is null)
        {
            return null;
        }

        var mutedNow = !muted;
        PushMuteFeedbackForChannel(binding.ChannelId, binding.Path, mutedNow);
        return BuildNotification(binding, updated, volume, mutedFallback: mutedNow);
    }

    private async Task<float> GetCurrentVolumeAsync(MidiBinding binding)
    {
        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        lock (_sync)
        {
            if (_volumeCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Channels.TryGetValue(SonarChannels.NormalizeChannel(binding.ChannelId), out var settings))
        {
            var state = binding.Path == SonarMixerPath.Streaming ? settings.Streaming : settings.Monitoring;
            if (state?.Volume is float v)
            {
                CacheVolume(binding, v);
                return v;
            }
        }

        return 0f;
    }

    private void CacheVolume(MidiBinding binding, float volume)
    {
        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        lock (_sync)
        {
            _volumeCache[key] = Math.Clamp(volume, 0f, 1f);
        }
    }

    private bool IsSameAsLastSonarWrite(MidiBinding binding, float volume)
    {
        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        var clamped = Math.Clamp(volume, 0f, 1f);
        lock (_sync)
        {
            return _lastSonarWrittenVolumes.TryGetValue(key, out var last)
                   && Math.Abs(last - clamped) <= FaderPriorityGuard.VolumeEpsilon;
        }
    }

    private void RememberSonarWrite(MidiBinding binding, float volume)
    {
        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        lock (_sync)
        {
            _lastSonarWrittenVolumes[key] = Math.Clamp(volume, 0f, 1f);
        }
    }

    private void RememberSonarWrite(string channelId, SonarMixerPath path, float volume)
    {
        var key = FaderPriorityGuard.ChannelKey(channelId, path);
        lock (_sync)
        {
            _lastSonarWrittenVolumes[key] = Math.Clamp(volume, 0f, 1f);
        }
    }

    private async Task RestoreAbsolutePositionsAsync()
    {
        lock (_sync)
        {
            if (_disposed || !_enabled || _absoluteRestoreRunning)
            {
                return;
            }

            _absoluteRestoreRunning = true;
        }

        try
        {
            if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
            {
                return;
            }

            var absolute = _mappingStore.Bindings
                .Where(MidiControlStateStore.IsPersistableAbsoluteVolume)
                .ToList();

            if (absolute.Count == 0)
            {
                return;
            }

            _controlStateStore.PruneTo(absolute.Select(b => b.BindingKey));

            var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
            var wroteSonar = false;

            foreach (var binding in absolute)
            {
                if (_disposed || !IsEnabled())
                {
                    return;
                }

                if (_controlStateStore.TryGet(binding.BindingKey, out var saved))
                {
                    _faderGuard.RememberHardwareVolume(binding, saved);
                    CacheVolume(binding, saved);
                    PublishAbsoluteVisual(binding, saved);

                    float? sonarVolume = null;
                    if (snapshot.Channels.TryGetValue(SonarChannels.NormalizeChannel(binding.ChannelId), out var existing))
                    {
                        var state = binding.Path == SonarMixerPath.Streaming
                            ? existing.Streaming
                            : existing.Monitoring;
                        sonarVolume = state?.Volume;
                    }

                    if (sonarVolume is float current
                        && Math.Abs(current - saved) <= FaderPriorityGuard.VolumeEpsilon)
                    {
                        RememberSonarWrite(binding, saved);
                        continue;
                    }

                    _faderGuard.MarkMidiOriginated(binding.ChannelId, binding.Path);
                    var updated = await _apiClient
                        .SetVolumeAsync(binding.ChannelId, saved, binding.Path)
                        .ConfigureAwait(false);
                    if (updated is not null)
                    {
                        RememberSonarWrite(binding, saved);
                        wroteSonar = true;
                    }

                    continue;
                }

                // No saved position — seed cache/guard from Sonar without overwriting.
                float? seedVolume = null;
                if (snapshot.Channels.TryGetValue(SonarChannels.NormalizeChannel(binding.ChannelId), out var settings))
                {
                    var state = binding.Path == SonarMixerPath.Streaming ? settings.Streaming : settings.Monitoring;
                    seedVolume = state?.Volume;
                }

                if (seedVolume is float v)
                {
                    CacheVolume(binding, v);
                    _faderGuard.RememberHardwareVolume(binding, v);
                    PublishAbsoluteVisual(binding, v);
                }
            }

            if (wroteSonar)
            {
                MixerChanged?.Invoke();
            }
        }
        catch
        {
            // Startup restore is best-effort.
        }
        finally
        {
            lock (_sync)
            {
                _absoluteRestoreRunning = false;
            }
        }
    }

    /// <summary>
    /// Last known absolute 0..1 for blueprint chrome: persisted hardware position, else channel cache.
    /// </summary>
    public bool TryGetAbsoluteVisual(MidiBinding binding, out float volume)
    {
        if (_controlStateStore.TryGet(binding.BindingKey, out volume))
        {
            return true;
        }

        if (!binding.HasSonarChannel)
        {
            volume = 0f;
            return false;
        }

        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        lock (_sync)
        {
            return _volumeCache.TryGetValue(key, out volume);
        }
    }

    private void PublishAbsoluteVisual(MidiBinding binding, float volume)
    {
        var clamped = Math.Clamp(volume, 0f, 1f);
        ControlFeedback?.Invoke(new MidiControlFeedback(
            binding.DeviceName,
            binding.Controller,
            MidiValueParser.VolumeToRaw(binding.IsPitchBend, clamped),
            clamped,
            binding.IsNote,
            binding.IsPitchBend));
    }

    private async Task PollSnapshotAsync()
    {
        if (!IsEnabled() || _disposed)
        {
            return;
        }

        try
        {
            if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
            {
                return;
            }

            var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
            var absolute = _mappingStore.Bindings
                .Where(b => b.HasSonarChannel && b.Mode == MidiValueMode.Absolute && !b.IsMotorized && !b.IsNote);

            foreach (var binding in absolute)
            {
                if (snapshot.Channels.TryGetValue(SonarChannels.NormalizeChannel(binding.ChannelId), out var settings))
                {
                    var state = binding.Path == SonarMixerPath.Streaming ? settings.Streaming : settings.Monitoring;
                    if (state?.Volume is float v)
                    {
                        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
                        lock (_sync)
                        {
                            if (!_volumeCache.ContainsKey(key))
                            {
                                _volumeCache[key] = v;
                            }
                        }
                    }
                }
            }

            if (!WasRecentlyActive())
            {
                _faderGuard.ObserveSnapshot(snapshot, absolute);
            }

            SyncHardwareFeedbackFromSnapshot(snapshot, force: false);
        }
        catch
        {
            // Snapshot poll is best-effort.
        }
    }

    /// <summary>Re-reads Sonar state and pushes all config-driven LED feedback.</summary>
    public Task RefreshHardwareFeedbackAsync(bool force = true) => RefreshMuteFeedbackAsync(force);

    /// <summary>
    /// Stages unsaved LED feedback for a control so the poller does not restore disk preset lamps.
    /// Pass <paramref name="feedback"/> null for Off.
    /// </summary>
    public void StageControlFeedback(string deviceName, string controlId, MidiControlFeedbackSpec? feedback)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(controlId))
        {
            return;
        }

        var key = FeedbackStageKey(deviceName, controlId);
        lock (_sync)
        {
            _feedbackStage[key] = MidiFeedbackResolver.Clone(feedback);
        }
    }

    /// <summary>
    /// Drops staged LED overrides (after Save / Discard / layout reload).
    /// </summary>
    public void ClearStagedControlFeedback(string? deviceName = null)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                _feedbackStage.Clear();
                return;
            }

            var prefix = MidiDevicePortNaming.CoreProductName(deviceName) + "|";
            foreach (var key in _feedbackStage.Keys.Where(k =>
                             k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _feedbackStage.Remove(key);
            }
        }
    }

    /// <summary>
    /// Sends a feedback template (materializes Pitch Bend match/mismatch from last hardware position).
    /// </summary>
    public bool TrySendFeedbackMessage(
        string deviceName,
        MidiLayoutControl control,
        MidiFeedbackMessage message,
        MidiBinding? binding = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(deviceName) || message is null)
        {
            return false;
        }

        // Pitch Bend match LED: extinguish only with a known physical position (never guess mid).
        if (control.IsPitchBend && message.Value < 64)
        {
            if (!TryGetHardwarePosition(deviceName, control, binding, out var hw))
            {
                return false;
            }

            SendPitchBendPosition(deviceName, control, hw);
            return true;
        }

        var hwGuess = ResolveHardwarePosition(binding, control);
        var wire = MidiFeedbackResolver.Materialize(message, control, hwGuess);
        return _outputHub.TrySend(deviceName, wire);
    }

    public async Task RefreshMuteFeedbackAsync(bool force = true)
    {
        if (_disposed || !IsEnabled())
        {
            return;
        }

        try
        {
            if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
            {
                SyncChannelAssignedFeedback(force);
                ExtinguishIdlePitchBendMatchLeds(force);
                return;
            }

            var snapshot = await _apiClient.GetMixerSnapshotAsync().ConfigureAwait(false);
            SyncHardwareFeedbackFromSnapshot(snapshot, force);
            ExtinguishIdlePitchBendMatchLeds(force);
        }
        catch
        {
            SyncChannelAssignedFeedback(force);
            ExtinguishIdlePitchBendMatchLeds(force);
        }
    }

    private void SyncHardwareFeedbackFromSnapshot(SonarMixerSnapshot snapshot, bool force)
    {
        SyncMuteFeedbackFromSnapshot(snapshot, force);
        SyncChannelAssignedFeedback(force);
    }

    private void SyncMuteFeedbackFromSnapshot(SonarMixerSnapshot snapshot, bool force)
    {
        var groups = EnumerateFeedbackBindings(MidiFeedbackSource.Mute)
            .GroupBy(
                b => FaderPriorityGuard.ChannelKey(b.ChannelId, b.Path),
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var sample = group.First();
            if (!snapshot.Channels.TryGetValue(SonarChannels.NormalizeChannel(sample.ChannelId), out var settings))
            {
                continue;
            }

            var state = sample.Path == SonarMixerPath.Streaming ? settings.Streaming : settings.Monitoring;
            var muted = state?.Muted == true;
            bool changed;
            lock (_sync)
            {
                changed = force
                          || !_muteCache.TryGetValue(group.Key, out var previous)
                          || previous != muted;
                _muteCache[group.Key] = muted;
            }

            if (!changed)
            {
                continue;
            }

            foreach (var binding in group)
            {
                ApplyMuteFeedback(binding, muted);
            }
        }
    }

    private void PushMuteFeedbackForChannel(string channelId, SonarMixerPath path, bool muted)
    {
        var key = FaderPriorityGuard.ChannelKey(channelId, path);
        lock (_sync)
        {
            _muteCache[key] = muted;
        }

        foreach (var binding in EnumerateFeedbackBindings(MidiFeedbackSource.Mute).Where(b =>
                     b.Path == path
                     && string.Equals(
                         SonarChannels.NormalizeChannel(b.ChannelId),
                         SonarChannels.NormalizeChannel(channelId),
                         StringComparison.OrdinalIgnoreCase)))
        {
            ApplyMuteFeedback(binding, muted);
        }
    }

    private void SyncChannelAssignedFeedback(bool force)
    {
        foreach (var deviceGroup in _mappingStore.Bindings.GroupBy(
                     b => b.DeviceName,
                     StringComparer.OrdinalIgnoreCase))
        {
            var layout = _presets.Resolve(deviceGroup.Key);
            foreach (var control in layout.Controls)
            {
                var spec = GetEffectiveFeedback(deviceGroup.Key, control);
                var key = $"{deviceGroup.Key}|{control.Id}";
                if (spec?.Source != MidiFeedbackSource.ChannelAssigned
                    || !MidiFeedbackResolver.TryResolveMessages(control, spec, out var on, out var off))
                {
                    bool clearLit;
                    lock (_sync)
                    {
                        clearLit = _channelAssignedLedCache.TryGetValue(key, out var previous) && previous;
                        _channelAssignedLedCache[key] = false;
                    }

                    if (clearLit)
                    {
                        ExtinguishFaderOrPadLamp(deviceGroup.Key, control, binding: null);
                    }

                    continue;
                }

                var binding = FindBindingForLayoutControl(deviceGroup, control);
                var active = binding?.HasSonarChannel == true;

                // Pitch Bend fader-top LEDs only support soft-takeover (PB out vs physical).
                // Never force a permanent mismatch — that makes them blink forever.
                if (control.IsPitchBend)
                {
                    SyncPitchBendSoftTakeoverLed(
                        deviceGroup.Key,
                        control,
                        binding,
                        active,
                        force,
                        cacheKey: key);
                    continue;
                }

                bool changed;
                lock (_sync)
                {
                    changed = force
                              || !_channelAssignedLedCache.TryGetValue(key, out var previous)
                              || previous != active;
                    _channelAssignedLedCache[key] = active;
                }

                if (!changed)
                {
                    continue;
                }

                if (spec.Style == MidiFeedbackStyle.Blink && active)
                {
                    TrySendFeedbackMessage(
                        deviceGroup.Key,
                        control,
                        _feedbackBlinkPhase ? on : off,
                        binding);
                }
                else
                {
                    TrySendFeedbackMessage(deviceGroup.Key, control, active ? on : off, binding);
                }
            }
        }
    }

    /// <summary>
    /// Drive / clear the strip match LED by sending software (Sonar) or hardware Pitch Bend.
    /// Hardware lights the LED only while those differ — never use intentional extremes.
    /// </summary>
    private void SyncPitchBendSoftTakeoverLed(
        string deviceName,
        MidiLayoutControl control,
        MidiBinding? binding,
        bool active,
        bool force,
        string cacheKey)
    {
        float target;
        if (active && binding is not null)
        {
            target = ResolveSoftwareVolume(binding, control);
        }
        else if (!TryGetHardwarePosition(deviceName, control, binding, out target))
        {
            lock (_sync)
            {
                _channelAssignedLedCache[cacheKey] = false;
            }

            return;
        }

        var msb = NormalizedToPitchBendMsb(target);
        bool changed;
        lock (_sync)
        {
            var wasActive = _channelAssignedLedCache.TryGetValue(cacheKey, out var prevActive) && prevActive;
            var lastMsb = _lastFaderLedMsbSent.TryGetValue(cacheKey, out var sent) ? sent : int.MinValue;
            changed = force || wasActive != active || lastMsb != msb;
            _channelAssignedLedCache[cacheKey] = active;
            if (changed)
            {
                _lastFaderLedMsbSent[cacheKey] = msb;
            }
        }

        if (!changed)
        {
            return;
        }

        SendPitchBendPosition(deviceName, control, target);
        if (force)
        {
            ClearLegacySelectNoteLamp(deviceName, control);
        }
    }

    private void ExtinguishFaderOrPadLamp(string deviceName, MidiLayoutControl control, MidiBinding? binding)
    {
        if (control.IsPitchBend)
        {
            if (TryGetHardwarePosition(deviceName, control, binding, out var hw))
            {
                SendPitchBendPosition(deviceName, control, hw);
            }

            return;
        }

        if (MidiFeedbackResolver.TryResolveMessages(control, control.Feedback, out _, out var diskOff))
        {
            TrySendFeedbackMessage(deviceName, control, diskOff, binding);
        }
    }

    /// <summary>
    /// Clear match LEDs on Pitch Bend faders that do not have active ChannelAssigned soft-takeover.
    /// Also echoes any remembered strip positions so stuck blink from older builds can clear.
    /// </summary>
    private void ExtinguishIdlePitchBendMatchLeds(bool force)
    {
        if (!force)
        {
            return;
        }

        foreach (var deviceName in _mappingStore.EnabledDevices)
        {
            var layout = _presets.Resolve(deviceName);
            var activeSoftTakeoverStrips = new HashSet<int>();
            foreach (var control in layout.Controls.Where(c => c.IsPitchBend))
            {
                var spec = GetEffectiveFeedback(deviceName, control);
                if (spec?.Source is MidiFeedbackSource.ChannelAssigned or MidiFeedbackSource.Mute)
                {
                    if (control.Controller is int strip)
                    {
                        activeSoftTakeoverStrips.Add(strip);
                    }

                    continue;
                }

                if (!TryGetHardwarePosition(deviceName, control, binding: null, out var hw))
                {
                    continue;
                }

                SendPitchBendPosition(deviceName, control, hw);
                ClearLegacySelectNoteLamp(deviceName, control);
            }

            // Echo remembered strips even if the layout control was removed / has no feedback.
            for (var strip = 0; strip < 8; strip++)
            {
                if (activeSoftTakeoverStrips.Contains(strip))
                {
                    continue;
                }

                var hwKey = FaderHardwareKey(deviceName, strip);
                float hw;
                lock (_sync)
                {
                    if (!_lastFaderHardware.TryGetValue(hwKey, out hw))
                    {
                        continue;
                    }
                }

                EchoFaderMatchLed(deviceName, strip, hw);
            }
        }
    }

    private static MidiBinding? FindBindingForLayoutControl(
        IEnumerable<MidiBinding> deviceBindings,
        MidiLayoutControl control)
    {
        var byId = deviceBindings.FirstOrDefault(b =>
            !string.IsNullOrWhiteSpace(b.ControlId)
            && string.Equals(b.ControlId, control.Id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        return deviceBindings.FirstOrDefault(b =>
            b.Controller == control.Controller
            && b.IsNote == control.IsNote
            && b.IsPitchBend == control.IsPitchBend);
    }

    private void ApplyMuteFeedback(MidiBinding binding, bool muted)
    {
        if (!TryResolveFeedback(binding, out var on, out var off))
        {
            return;
        }

        var control = FindLayoutControlForBinding(binding);
        if (control is null)
        {
            return;
        }

        var style = GetEffectiveFeedback(binding.DeviceName, control)?.Style
                    ?? MidiFeedbackStyle.Solid;
        if (!muted)
        {
            if (control.IsPitchBend
                && TryGetHardwarePosition(binding.DeviceName, control, binding, out var hw))
            {
                SendPitchBendPosition(binding.DeviceName, control, hw);
                return;
            }

            TrySendFeedbackMessage(binding.DeviceName, control, off, binding);
            return;
        }

        if (style == MidiFeedbackStyle.Blink)
        {
            if (control.IsPitchBend)
            {
                // Hardware already blinks on mismatch — send soft-takeover Sonar vs match.
                TrySendFeedbackMessage(
                    binding.DeviceName,
                    control,
                    _feedbackBlinkPhase ? on : off,
                    binding);
                return;
            }

            TrySendFeedbackMessage(
                binding.DeviceName,
                control,
                _feedbackBlinkPhase ? on : off,
                binding);
            return;
        }

        TrySendFeedbackMessage(binding.DeviceName, control, on, binding);
    }

    private void TickFeedbackBlink()
    {
        if (_disposed || !IsEnabled())
        {
            return;
        }

        _feedbackBlinkPhase = !_feedbackBlinkPhase;
        Dictionary<string, bool> muteSnapshot;
        lock (_sync)
        {
            muteSnapshot = new Dictionary<string, bool>(_muteCache, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var binding in EnumerateFeedbackBindings(MidiFeedbackSource.Mute))
        {
            var control = FindLayoutControlForBinding(binding);
            var spec = GetEffectiveFeedback(binding.DeviceName, control);
            if (spec?.Style != MidiFeedbackStyle.Blink || control is null)
            {
                continue;
            }

            // Don't PWM Pitch Bend match LEDs — hardware already blinks on mismatch.
            if (control.IsPitchBend)
            {
                continue;
            }

            var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
            if (!muteSnapshot.TryGetValue(key, out var muted) || !muted)
            {
                continue;
            }

            if (!TryResolveFeedback(binding, out var on, out var off))
            {
                continue;
            }

            TrySendFeedbackMessage(
                binding.DeviceName,
                control,
                _feedbackBlinkPhase ? on : off,
                binding);
        }

        foreach (var deviceGroup in _mappingStore.Bindings.GroupBy(
                     b => b.DeviceName,
                     StringComparer.OrdinalIgnoreCase))
        {
            var layout = _presets.Resolve(deviceGroup.Key);
            foreach (var control in layout.Controls)
            {
                var spec = GetEffectiveFeedback(deviceGroup.Key, control);
                if (spec?.Source != MidiFeedbackSource.ChannelAssigned
                    || spec.Style != MidiFeedbackStyle.Blink
                    || control.IsPitchBend
                    || !MidiFeedbackResolver.TryResolveMessages(control, spec, out var on, out var off))
                {
                    continue;
                }

                var binding = FindBindingForLayoutControl(deviceGroup, control);
                if (binding?.HasSonarChannel != true)
                {
                    continue;
                }

                TrySendFeedbackMessage(
                    deviceGroup.Key,
                    control,
                    _feedbackBlinkPhase ? on : off,
                    binding);
            }
        }
    }

    /// <summary>
    /// After a physical fader move, re-send Sonar Pitch Bend (soft takeover) or echo hardware (clear).
    /// </summary>
    private void RefreshFaderMatchLedAfterHardwareMove(MidiBinding binding)
    {
        if (!binding.IsPitchBend)
        {
            return;
        }

        var control = FindLayoutControlForBinding(binding);
        var spec = GetEffectiveFeedback(binding.DeviceName, control);
        if (control is null)
        {
            if (TryGetHardwarePosition(binding.DeviceName, null, binding, out var hwOnly))
            {
                EchoFaderMatchLed(binding.DeviceName, binding.Controller, hwOnly);
            }

            return;
        }

        if (spec?.Source == MidiFeedbackSource.ChannelAssigned && binding.HasSonarChannel)
        {
            SyncPitchBendSoftTakeoverLed(
                binding.DeviceName,
                control,
                binding,
                active: true,
                force: true,
                cacheKey: $"{binding.DeviceName}|{control.Id}");
            return;
        }

        if (spec?.Source == MidiFeedbackSource.Mute && binding.HasSonarChannel)
        {
            var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
            bool muted;
            lock (_sync)
            {
                if (!_muteCache.TryGetValue(key, out muted))
                {
                    return;
                }
            }

            ApplyMuteFeedback(binding, muted);
            return;
        }

        if (TryGetHardwarePosition(binding.DeviceName, control, binding, out var hw))
        {
            SendPitchBendPosition(binding.DeviceName, control, hw);
        }
    }

    private void RememberFaderHardware(string deviceName, int stripIndex, float normalized)
    {
        var key = FaderHardwareKey(deviceName, stripIndex);
        lock (_sync)
        {
            _lastFaderHardware[key] = Math.Clamp(normalized, 0f, 1f);
        }
    }

    private void EchoFaderMatchLed(string deviceName, int stripIndex, float normalized)
    {
        var control = new MidiLayoutControl
        {
            Id = $"pb{stripIndex}",
            Type = MidiControlType.Fader,
            Controller = stripIndex,
            IsPitchBend = true
        };
        SendPitchBendPosition(deviceName, control, normalized);
    }

    private void SendPitchBendPosition(string deviceName, MidiLayoutControl control, float normalized)
    {
        var strip = Math.Clamp(control.Controller ?? 0, 0, 15);
        var msb = NormalizedToPitchBendMsb(normalized);
        _outputHub.TrySend(
            deviceName,
            new MidiFeedbackMessage
            {
                Kind = MidiFeedbackKind.PitchBend,
                Controller = 0,
                Value = msb,
                Channel = strip + 1
            });
    }

    private static int NormalizedToPitchBendMsb(float normalized) =>
        (int)Math.Round(Math.Clamp(normalized, 0f, 1f) * 127f);

    private float ResolveSoftwareVolume(MidiBinding binding, MidiLayoutControl control)
    {
        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        lock (_sync)
        {
            if (_volumeCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        if (_controlStateStore.TryGet(binding.BindingKey, out var stored))
        {
            return stored;
        }

        return ResolveHardwarePosition(binding, control);
    }

    private float ResolveHardwarePosition(MidiBinding? binding, MidiLayoutControl? control)
    {
        var device = binding?.DeviceName ?? string.Empty;
        if (TryGetHardwarePosition(device, control, binding, out var hw))
        {
            return hw;
        }

        return 0.5f;
    }

    private bool TryGetHardwarePosition(
        string deviceName,
        MidiLayoutControl? control,
        MidiBinding? binding,
        out float hardwareNormalized)
    {
        if (control?.IsPitchBend == true && control.Controller is int strip)
        {
            var hwKey = FaderHardwareKey(deviceName, strip);
            lock (_sync)
            {
                if (_lastFaderHardware.TryGetValue(hwKey, out hardwareNormalized))
                {
                    return true;
                }
            }
        }
        else if (binding is { IsPitchBend: true })
        {
            var hwKey = FaderHardwareKey(deviceName, binding.Controller);
            lock (_sync)
            {
                if (_lastFaderHardware.TryGetValue(hwKey, out hardwareNormalized))
                {
                    return true;
                }
            }
        }

        if (binding is not null && _controlStateStore.TryGet(binding.BindingKey, out hardwareNormalized))
        {
            return true;
        }

        if (binding is not null)
        {
            var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
            lock (_sync)
            {
                if (_volumeCache.TryGetValue(key, out hardwareNormalized))
                {
                    return true;
                }
            }
        }

        hardwareNormalized = 0f;
        return false;
    }

    private static string FaderHardwareKey(string deviceName, int stripIndex) =>
        $"{MidiDevicePortNaming.CoreProductName(deviceName)}|{stripIndex}";

    /// <summary>
    /// Clears Select-pad lamps if an older build had driven ChannelAssigned via MCU Select notes.
    /// </summary>
    private void ClearLegacySelectNoteLamp(string deviceName, MidiLayoutControl control)
    {
        if (!control.IsPitchBend)
        {
            return;
        }

        var strip = Math.Clamp(control.Controller ?? 0, 0, 7);
        _outputHub.TrySend(
            deviceName,
            new MidiFeedbackMessage
            {
                Kind = MidiFeedbackKind.Note,
                Controller = 24 + strip,
                Value = 0,
                Channel = 1
            });
    }

    private IEnumerable<MidiBinding> EnumerateFeedbackBindings(MidiFeedbackSource source) =>
        _mappingStore.Bindings.Where(b =>
        {
            if (source == MidiFeedbackSource.Mute && !b.HasSonarChannel)
            {
                return false;
            }

            var control = FindLayoutControlForBinding(b);
            var spec = GetEffectiveFeedback(b.DeviceName, control);
            return spec?.Source == source
                   && control is not null
                   && MidiFeedbackResolver.TryResolveMessages(control, spec, out _, out _);
        });

    private bool TryResolveFeedback(
        MidiBinding binding,
        out MidiFeedbackMessage on,
        out MidiFeedbackMessage off)
    {
        on = null!;
        off = null!;
        var control = FindLayoutControlForBinding(binding);
        if (control is null)
        {
            return false;
        }

        var spec = GetEffectiveFeedback(binding.DeviceName, control);
        return MidiFeedbackResolver.TryResolveMessages(control, spec, out on, out off);
    }

    private MidiControlFeedbackSpec? GetEffectiveFeedback(string deviceName, MidiLayoutControl? control)
    {
        if (control is null)
        {
            return null;
        }

        var key = FeedbackStageKey(deviceName, control.Id);
        lock (_sync)
        {
            if (_feedbackStage.TryGetValue(key, out var staged))
            {
                return staged;
            }
        }

        return control.Feedback;
    }

    private static string FeedbackStageKey(string deviceName, string controlId) =>
        $"{MidiDevicePortNaming.CoreProductName(deviceName)}|{controlId}";

    private MidiLayoutControl? FindLayoutControlForBinding(MidiBinding binding)
    {
        var layout = _presets.Resolve(binding.DeviceName);
        if (!string.IsNullOrWhiteSpace(binding.ControlId))
        {
            var byId = layout.Controls.FirstOrDefault(c =>
                string.Equals(c.Id, binding.ControlId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        return layout.Controls.FirstOrDefault(c =>
            c.Controller == binding.Controller
            && c.IsNote == binding.IsNote
            && c.IsPitchBend == binding.IsPitchBend
            && GetEffectiveFeedback(binding.DeviceName, c) is { Source: not MidiFeedbackSource.None });
    }

    private void OnRollbackRequested(
        string channelId,
        float volume,
        SonarMixerPath path,
        VolumeNotificationState notification)
    {
        _ = ApplyRollbackAsync(channelId, volume, path, notification);
    }

    private async Task ApplyRollbackAsync(
        string channelId,
        float volume,
        SonarMixerPath path,
        VolumeNotificationState notification)
    {
        try
        {
            if (!await _apiClient.EnsureConnectedAsync().ConfigureAwait(false))
            {
                return;
            }

            _faderGuard.MarkMidiOriginated(channelId, path);
            var updated = await _apiClient.SetVolumeAsync(channelId, volume, path).ConfigureAwait(false);
            if (updated is null)
            {
                return;
            }

            CacheVolume(new MidiBinding { ChannelId = channelId, Path = path }, volume);
            RememberSonarWrite(channelId, path, volume);

            // Rollback restores the last absolute hardware position — persist it for the owning binding(s).
            foreach (var binding in _mappingStore.Bindings.Where(MidiControlStateStore.IsPersistableAbsoluteVolume))
            {
                if (string.Equals(
                        SonarChannels.NormalizeChannel(binding.ChannelId),
                        SonarChannels.NormalizeChannel(channelId),
                        StringComparison.OrdinalIgnoreCase)
                    && binding.Path == path)
                {
                    _controlStateStore.SetFromBinding(binding, volume);
                }
            }

            MixerChanged?.Invoke();
            VolumeAdjusted?.Invoke(notification);
        }
        catch
        {
            // Rollback is best-effort.
        }
    }

    private static VolumeNotificationState BuildNotification(
        MidiBinding binding,
        IReadOnlyDictionary<string, SonarChannelSettings> updated,
        float volumeFallback,
        bool mutedFallback)
    {
        var channel = SonarChannels.NormalizeChannel(binding.ChannelId);
        if (updated.TryGetValue(channel, out var settings))
        {
            var state = binding.Path == SonarMixerPath.Streaming ? settings.Streaming : settings.Monitoring;
            return new VolumeNotificationState(
                channel,
                state?.Volume ?? volumeFallback,
                state?.Muted ?? mutedFallback,
                Path: binding.Path);
        }

        return new VolumeNotificationState(channel, volumeFallback, mutedFallback, Path: binding.Path);
    }

    private VolumeNotificationState BuildLiveNotification(MidiBinding binding, float volume)
    {
        var channel = SonarChannels.NormalizeChannel(binding.ChannelId);
        var key = FaderPriorityGuard.ChannelKey(binding.ChannelId, binding.Path);
        var muted = false;
        lock (_sync)
        {
            _muteCache.TryGetValue(key, out muted);
        }

        return new VolumeNotificationState(channel, volume, muted, Path: binding.Path);
    }

    private TimeSpan GetSonarWriteDelay()
    {
        var elapsedMs = (DateTime.UtcNow - _lastSonarWriteUtc).TotalMilliseconds;
        var remainingMs = SonarWriteMinIntervalMs - elapsedMs;
        return remainingMs > 0 ? TimeSpan.FromMilliseconds(remainingMs) : TimeSpan.Zero;
    }

    private bool IsEnabled()
    {
        lock (_sync)
        {
            return _enabled && !_disposed;
        }
    }
}
