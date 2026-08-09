namespace SonarQuickMixer.Services;

public readonly record struct VolumeNotificationState(
    string ChannelId,
    float Volume,
    bool IsMuted,
    string? Message = null);
