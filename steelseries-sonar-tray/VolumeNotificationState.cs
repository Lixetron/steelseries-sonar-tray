namespace SteelSeries.SonarTray;

public readonly record struct VolumeNotificationState(string ChannelId, float Volume, bool IsMuted);
