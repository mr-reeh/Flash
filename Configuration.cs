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

    /// <summary>Base64-encoded Glamourer design string, exported from the Glamourer GUI.</summary>
    public string GlamourerDesignBase64 { get; set; } = string.Empty;

    /// <summary>If true, the outfit is reverted back to the player's real gear after the emote finishes.</summary>
    public bool RevertAfterEmote { get; set; } = true;

    /// <summary>How long (seconds) to wait before reverting, if RevertAfterEmote is true.</summary>
    public float RevertDelaySeconds { get; set; } = 3.0f;

    /// <summary>Only trigger for the local player, never for other actors performing the emote nearby.</summary>
    public bool LocalPlayerOnly { get; set; } = true;

    /// <summary>Whether this mapping is currently active.</summary>
    public bool Enabled { get; set; } = true;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>All configured emote -> gear mappings.</summary>
    public List<EmoteGearEntry> Entries { get; set; } = new();

    /// <summary>Master on/off switch for the whole plugin.</summary>
    public bool PluginEnabled { get; set; } = true;

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
