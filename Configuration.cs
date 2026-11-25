using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace JustDance;

public enum PlaybackLoopMode
{
    Auto = 0,
    Loop = 1,
    OneShot = 2,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>
    /// User-selected virtual folder inside Penumbra (e.g. "03_Dance" or "03_Dance/01_Jpop").
    /// Empty string "" means Root (all Penumbra mods); null means not selected yet.
    /// </summary>
    public string? SelectedPenumbraFolder { get; set; }

    /// <summary>When scanning, mirror Penumbra subfolders into JustDance folders.</summary>
    public bool MirrorPenumbraSubfolders { get; set; } = true;

    /// <summary>Remove entries whose Penumbra mod no longer exists during rescan.</summary>
    public bool PruneMissingOnScan { get; set; } = true;

    /// <summary>Always trigger a quick jump to stop any active emote before starting the clicked animation.</summary>
    public bool AlwaysJumpBeforeStart { get; set; } = false;

    /// <summary>Print status messages in the chat log.</summary>
    public bool ShowChatMessages { get; set; } = false;

    public List<DanceFolder> Folders { get; set; } = new();
    public List<DanceEntry> Entries { get; set; } = new();

    /// <summary>Last active temporary mod keys ("collectionId|modDir|modName") for crash cleanup on next startup.</summary>
    public List<string> LastTempModKeys { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}

[Serializable]
public class DanceFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Folder";
    public string? ParentFolderId { get; set; }
    public bool IsCollapsed { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Serializable]
public class PinnedModControl
{
    public string GroupName { get; set; } = "";

    /// <summary>True for Penumbra Multi groups (checkboxes); false for Single groups (dropdown).</summary>
    public bool IsMulti { get; set; }

    /// <summary>For Multi checkboxes pinned individually; null for Single dropdowns.</summary>
    public string? OptionName { get; set; }
}

[Serializable]
public class DanceEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";

    /// <summary>Penumbra mod directory name (unique mod key in Penumbra).</summary>
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public string PenumbraFolder { get; set; } = "";

    /// <summary>User-selected Penumbra settings pinned to the MainWindow row next to the dance name.</summary>
    public List<PinnedModControl> PinnedControls { get; set; } = new();

    /// <summary>True when the mod has a Penumbra option group that can enable/disable .scd music.</summary>
    public bool HasMusicToggle { get; set; } = false;
    public string MusicGroupName { get; set; } = "";
    public bool MusicIsMulti { get; set; } = false;
    public List<string> MusicOnOptions { get; set; } = new();
    public string? MusicOffOption { get; set; }
    public string? PreferredMusicOption { get; set; }

    /// <summary>Cached state of the mod's Penumbra music setting (refreshed from Penumbra).</summary>
    public bool IsMusicEnabled { get; set; } = true;

    public string? FolderId { get; set; }
    public PlaybackLoopMode LoopMode { get; set; } = PlaybackLoopMode.Auto;
    public bool IsFavorite { get; set; } = false;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
