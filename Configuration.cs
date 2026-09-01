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

    /// <summary>How long (seconds) to wait after the emote is detected before changing gear.</summary>
    public float TriggerDelaySeconds { get; set; } = 0f;

    /// <summary>If true, gear is force-reverted after DurationSeconds even if the animation is
    /// still playing - use this to end a change early. If false, gear only reverts once the
    /// animation actually finishes (see NativeCharacterHelper).</summary>
    public bool UseDuration { get; set; } = false;

    /// <summary>How long (seconds) to stay changed before force-reverting, if UseDuration is true.</summary>
    public float DurationSeconds { get; set; } = 5.0f;

    /// <summary>Only trigger for the local player, never for other actors nearby.</summary>
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
