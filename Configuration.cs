using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Flash;

/// <summary>
/// Which gear the character ends up wearing while a mapped emote is active.
/// </summary>
public enum StripMode
{
    /// <summary>Every slot set to "nothing" - fully bare.</summary>
    Smallclothes,

    /// <summary>Every slot set to the corresponding Emperor's New Set piece.</summary>
    EmperorsSet,
}

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

    /// <summary>How long (seconds) to wait after the emote is detected before stripping gear.</summary>
    public float TriggerDelaySeconds { get; set; } = 0f;

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

    /// <summary>Whether stripped gear becomes "nothing" or the Emperor's New Set. Global -
    /// applies to every mapping, not set per-entry.</summary>
    public StripMode StripMode { get; set; } = StripMode.Smallclothes;

    /// <summary>When true, every emote seen (native hook or chat) is logged and echoed to
    /// chat (whether it matched a configured entry or not), so you can confirm detection
    /// is actually firing. Toggle via '/emotegear debug'.</summary>
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
