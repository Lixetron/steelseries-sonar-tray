using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Services;

/// <summary>
/// Stable-order upsert list for multi-channel volume overlay rows.
/// First appearance defines order; later updates replace the same ChannelId in place.
/// </summary>
public sealed class VolumeOverlayChannelList
{
    private readonly List<VolumeNotificationState> _channels = [];
    private readonly Dictionary<string, int> _indexByChannel = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VolumeNotificationState> Channels => _channels;

    public int Count => _channels.Count;

    public void Upsert(VolumeNotificationState state)
    {
        var channelId = SonarChannels.NormalizeChannel(state.ChannelId);
        var normalized = state with { ChannelId = channelId };

        if (_indexByChannel.TryGetValue(channelId, out var index))
        {
            _channels[index] = normalized;
            return;
        }

        _indexByChannel[channelId] = _channels.Count;
        _channels.Add(normalized);
    }

    public void Clear()
    {
        _channels.Clear();
        _indexByChannel.Clear();
    }

    public IReadOnlyList<VolumeNotificationState> Snapshot() => _channels.ToList();
}
