namespace SonarQuickMixer;

public readonly record struct VolumeNotificationState(string ChannelId, float Volume, bool IsMuted);
