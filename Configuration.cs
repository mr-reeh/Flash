using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Flash;

/// <summary>
/// One emote -> gear mapping.
/// </summary>
[Serializable]
public class EmoteGearEntry
{
    /// <summary>Row/Id of the emote in Lumina's Emote sheet.</summary>
    public uint EmoteId { get; set; }

    /// <summary>Display name cached for the config UI (not authoritative, just convenience).</summary>
    public string EmoteName { get; set; } = string.Empty;

    /// <summary>If true, gear is restored after the emote finishes (after RevertDelaySeconds).</summary>
    public bool RevertAfterEmote { get; set; } = true;

    /// <summary>How long (seconds) to stay stripped before restoring, if RevertAfterEmote is true.</summary>
    public float RevertDelaySeconds { get; set; } = 5.0f;

    /// <summary>Only trigger for the local player, never for other actors performing the emote nearby.</summary>
    public bool LocalPlayerOnly { get; set; } = true;

    /// <summary>Whether this mapping is currently active.</summary>
    public bool Enabled { get; set; } = true;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>All configured emote -> strip-gear mappings.</summary>
    public List<EmoteGearEntry> Entries { get; set; } = new();

    /// <summary>Master on/off switch for the whole plugin.</summary>
    public bool PluginEnabled { get; set; } = true;

    /// <summary>When true, every StandardEmote/CustomEmote chat line seen is logged and
    /// echoed to chat (whether it matched a configured entry or not), so you can confirm
    /// detection is actually firing. Toggle via '/emotegear debug'.</summary>
    public bool DebugMode { get; set; } = false;

    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        this.pluginInterface = pi;
    }

    public void Save()
    {
        this.pluginInterface?.SavePluginConfig(this);
    }
}
