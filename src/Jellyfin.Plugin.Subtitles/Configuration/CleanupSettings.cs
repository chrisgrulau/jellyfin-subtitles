namespace Jellyfin.Plugin.Subtitles.Configuration;

/// <summary>
/// Clean-up settings. Adverts and sound descriptions are basic settings; the rest are advanced.
/// </summary>
public class CleanupSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether subtitle-site adverts and credit lines ("Subtitles by …") are removed.
    /// </summary>
    public bool RemoveAdverts { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether sound descriptions (<c>[door slams]</c>), speaker labels and music-only
    /// lines are removed. This changes what viewers read, so it follows the wording policy.
    /// </summary>
    public bool RemoveSoundDescriptions { get; set; }

    /// <summary>
    /// Gets or sets what happens to advert and credit removal (automatic by default: those lines are never dialogue).
    /// </summary>
    public ChangePolicy AdvertRemoval { get; set; } = ChangePolicy.Automatic;

    /// <summary>
    /// Gets or sets a value indicating whether a line repeated back to back is merged into one (follows the wording policy).
    /// </summary>
    public bool MergeDuplicateLines { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether lines that run into the next one by up to a second are shortened (follows
    /// the timing policy).
    /// </summary>
    public bool FixOverlaps { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether lines shown too briefly to read are lengthened, never into the next line
    /// (follows the timing policy).
    /// </summary>
    public bool LengthenFlashLines { get; set; } = true;

    /// <summary>Gets or sets how short (in milliseconds) a line must be to count as too brief to read.</summary>
    public int FlashThresholdMs { get; set; } = 500;

    /// <summary>Gets or sets how long (in milliseconds) such a line is lengthened to, where there's room.</summary>
    public int FlashTargetMs { get; set; } = 833;

    /// <summary>
    /// Gets or sets a value indicating whether timing clean-up also applies to ASS signs, karaoke and effects, which are
    /// short and overlap on purpose. Off by default.
    /// </summary>
    public bool FixTypesetTiming { get; set; }
}
