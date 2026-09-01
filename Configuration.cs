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
/// What kind of game event triggers a mapping.
/// </summary>
public enum TriggerType
{
    /// <summary>An emote (via the emote wheel, /emote, or a macro).</summary>
    Emote,

    /// <summary>Using a combat/other action - see ActionHook.cs (not yet implemented).</summary>
    Action,
}

/// <summary>
/// One trigger -> gear mapping.
/// </summary>
[Serializable]
public class EmoteGearEntry
{
    /// <summary>Whether this entry fires on an emote or an action.</summary>
    public TriggerType TriggerType { get; set; } = TriggerType.Emote;

    /// <summary>Row/Id of the emote in Lumina's Emote sheet. Used when TriggerType is Emote.</summary>
    public uint EmoteId { get; set; }

    /// <summary>Display name cached for the config UI (not authoritative, just convenience).</summary>
    public string EmoteName { get; set; } = string.Empty;

    /// <summary>Row/Id of the action in Lumina's Action sheet. Used when TriggerType is Action.</summary>
    public uint ActionId { get; set; }

    /// <summary>Display name cached for the config UI (not authoritative, just convenience).</summary>
    public string ActionName { get; set; } = string.Empty;

    /// <summary>How long (seconds) to wait after the trigger is detected before changing gear.</summary>
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

    /// <summary>All configured trigger -> gear mappings.</summary>
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
