namespace Jellyfin.Plugin.Subtitles.Configuration;

/// <summary>
/// What happens to a proposed change to a subtitle file.
/// </summary>
public enum ChangePolicy
{
    /// <summary>Apply it automatically (it can always be undone).</summary>
    Automatic = 0,

    /// <summary>Hold it for review on the plugin page.</summary>
    Review,
}
