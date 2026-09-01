using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Flash;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Flash";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    public Configuration Configuration { get; }
    public GlamourerIpc Glamourer { get; }
    public PluginUi Ui { get; }

    private readonly EmoteWatcher emoteWatcher;
    private readonly EmoteHook emoteHook;

    // Emotes waiting out their trigger delay before gear gets stripped/swapped.
    private readonly List<PendingStrip> pendingStrips = new();

    // Characters currently wearing Flash-altered gear, so a later unmatched emote knows
    // to revert them. Gear persists until the animation actually changes to something
    // not configured - there is no timer.
    private readonly HashSet<ulong> alteredCharacters = new();

    private const string CommandName = "/emotegear";

    private readonly record struct PendingStrip(ulong GameObjectId, EmoteGearEntry Entry, DateTime StripAt);

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);

        this.Glamourer = new GlamourerIpc(PluginInterface, Log);
        this.Glamourer.ResolveEmperorsSetItemIds(DataManager);

        this.emoteWatcher = new EmoteWatcher(ChatGui, Log);
        this.emoteWatcher.EmoteMessageSeen += this.OnEmoteMessageSeen;

        this.emoteHook = new EmoteHook(GameInteropProvider, Log);
        this.emoteHook.LocalPlayerEmoteExecuted += this.OnLocalPlayerEmoteExecuted;

        this.Ui = new PluginUi(this);
        PluginInterface.UiBuilder.Draw += this.Ui.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += () => this.Ui.IsOpen = true;

        Framework.Update += this.OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the emote config window. '/emotegear toggle' enables/disables the plugin. " +
                          "'/emotegear debug' toggles verbose logging of every emote detected, to chat and /xllog.",
        });
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();

        if (string.Equals(trimmed, "toggle", StringComparison.OrdinalIgnoreCase))
        {
            this.Configuration.PluginEnabled = !this.Configuration.PluginEnabled;
            this.Configuration.Save();
            Log.Information($"[Flash] Plugin {(this.Configuration.PluginEnabled ? "enabled" : "disabled")}.");
            return;
        }

        if (string.Equals(trimmed, "debug", StringComparison.OrdinalIgnoreCase))
        {
            this.Configuration.DebugMode = !this.Configuration.DebugMode;
            this.Configuration.Save();
            ChatGui.Print($"[Flash] Debug mode {(this.Configuration.DebugMode ? "ON" : "OFF")} - " +
                          "every emote detected will be logged here" +
                          (this.Configuration.DebugMode ? "." : " (now suppressed)."));
            return;
        }

        this.Ui.IsOpen = !this.Ui.IsOpen;
    }

    /// <summary>
    /// Called from EmoteHook whenever the local player executes any emote - native hook,
    /// exact numeric EmoteId, no dependency on chat settings. This is the primary/
    /// preferred detection path for local-player entries.
    /// </summary>
    private void OnLocalPlayerEmoteExecuted(ushort emoteId)
    {
        if (this.Configuration.DebugMode)
        {
            var line = $"[Flash] native: local player used emote id {emoteId}";
            Log.Information(line);
            ChatGui.Print(line);
        }

        if (!this.Configuration.PluginEnabled)
        {
            if (this.Configuration.DebugMode)
                ChatGui.Print("[Flash] ...ignored, plugin is disabled ('/emotegear toggle' to enable).");

            return;
        }

        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            if (this.Configuration.DebugMode)
                ChatGui.Print("[Flash] ...local player object unavailable, skipping.");

            return;
        }

        var match = this.FindMatchById(emoteId);
        this.HandleEmoteForCharacter(match, localPlayer);
    }

    /// <summary>
    /// Called from EmoteWatcher whenever a StandardEmote/CustomEmote line appears in
    /// chat. Local-player matches are skipped here - EmoteHook's native detection is
    /// authoritative for those, and both firing would double-handle the same emote. This
    /// path only matters for LocalPlayerOnly=false entries, detecting other characters'
    /// emotes, and only when the client's "Log Emotes" setting is on.
    ///
    /// Matching here is a case-insensitive substring check of the configured emote's
    /// name against the rendered chat text (see EmoteWatcher.cs for why - chat text
    /// doesn't carry a numeric EmoteId like the native hook does).
    /// </summary>
    private void OnEmoteMessageSeen(string senderName, string messageText)
    {
        if (this.Configuration.DebugMode)
        {
            var line = $"[Flash] chat seen: sender='{senderName}' text='{messageText}'";
            Log.Information(line);
            ChatGui.Print(line);
        }

        if (!this.Configuration.PluginEnabled)
        {
            if (this.Configuration.DebugMode)
                ChatGui.Print("[Flash] ...ignored, plugin is disabled ('/emotegear toggle' to enable).");

            return;
        }

        var localPlayer = ObjectTable.LocalPlayer;

        // Self-performed emotes typically render without a separate sender name (the
        // "You " is baked into the message text itself), so an empty sender means it's
        // very likely the local player. Otherwise compare names directly.
        var isLocalPlayer = string.IsNullOrEmpty(senderName)
            || (localPlayer != null && string.Equals(senderName, localPlayer.Name.TextValue, StringComparison.Ordinal));

        if (isLocalPlayer)
        {
            if (this.Configuration.DebugMode)
                ChatGui.Print("[Flash] ...ignored, this was the local player (handled by the native hook instead).");

            return;
        }

        var target = this.FindCharacterByName(senderName);
        if (target == null)
        {
            if (this.Configuration.DebugMode)
                ChatGui.Print($"[Flash] ...couldn't resolve a character for sender '{senderName}'.");

            return;
        }

        var match = this.FindMatchByText(messageText);
        this.HandleEmoteForCharacter(match, target);
    }

    private EmoteGearEntry? FindMatchById(ushort emoteId)
    {
        foreach (var entry in this.Configuration.Entries)
        {
            if (entry.Enabled && entry.EmoteId == emoteId)
                return entry;
        }

        return null;
    }

    private EmoteGearEntry? FindMatchByText(string messageText)
    {
        foreach (var entry in this.Configuration.Entries)
        {
            if (entry.Enabled
                && !entry.LocalPlayerOnly
                && !string.IsNullOrWhiteSpace(entry.EmoteName)
                && messageText.Contains(entry.EmoteName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Shared handling for both detection paths once a target character and (possibly
    /// null) matched entry are known. If matched, schedules a strip. If not matched and
    /// the character currently has Flash-altered gear, that means the animation changed
    /// to something not configured, so gear is reverted immediately. Either way, any
    /// still-pending strip for this character from a previous emote is cancelled first,
    /// so a rapid animation change can't apply a stale strip after the fact.
    /// </summary>
    private void HandleEmoteForCharacter(EmoteGearEntry? match, ICharacter target)
    {
        this.pendingStrips.RemoveAll(p => p.GameObjectId == target.GameObjectId);

        if (match != null)
        {
            if (this.Configuration.DebugMode)
            {
                ChatGui.Print($"[Flash] ...matched entry '{match.EmoteName}' (id {match.EmoteId}) - " +
                              $"scheduling strip on '{target.Name.TextValue}' in {match.TriggerDelaySeconds:0.0}s.");
            }

            this.pendingStrips.Add(new PendingStrip(
                target.GameObjectId,
                match,
                DateTime.UtcNow.AddSeconds(match.TriggerDelaySeconds)));

            return;
        }

        if (this.Configuration.DebugMode)
            ChatGui.Print("[Flash] ...no configured emote matched.");

        if (this.alteredCharacters.Remove(target.GameObjectId))
        {
            if (this.Configuration.DebugMode)
                ChatGui.Print($"[Flash] ...animation changed, reverting '{target.Name.TextValue}'.");

            this.Glamourer.Revert(target);
        }
    }

    private ICharacter? FindCharacterByName(string name)
    {
        foreach (var obj in ObjectTable)
        {
            if (obj is ICharacter character && string.Equals(character.Name.TextValue, name, StringComparison.Ordinal))
                return character;
        }

        return null;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (this.pendingStrips.Count == 0)
            return;

        var now = DateTime.UtcNow;
        for (var i = this.pendingStrips.Count - 1; i >= 0; i--)
        {
            var pending = this.pendingStrips[i];
            if (pending.StripAt > now)
                continue;

            this.pendingStrips.RemoveAt(i);

            ICharacter? target = null;
            foreach (var obj in ObjectTable)
            {
                if (obj is ICharacter character && character.GameObjectId == pending.GameObjectId)
                {
                    target = character;
                    break;
                }
            }

            if (target == null)
            {
                if (this.Configuration.DebugMode)
                    ChatGui.Print("[Flash] ...couldn't find the character at strip time, skipping.");

                continue;
            }

            if (!this.Glamourer.IsAvailable())
            {
                Log.Warning("[Flash] Glamourer is not installed/loaded - cannot strip gear.");

                if (this.Configuration.DebugMode)
                    ChatGui.Print("[Flash] ...Glamourer isn't available, aborting.");

                continue;
            }

            var stripped = this.Glamourer.StripAllGear(
                target,
                this.Configuration.StripMode,
                onSlotResult: this.Configuration.DebugMode
                    ? (line => ChatGui.Print($"[Flash]   {line}"))
                    : null);

            if (this.Configuration.DebugMode)
                ChatGui.Print($"[Flash] ...StripAllGear on '{target.Name.TextValue}' " +
                              $"({this.Configuration.StripMode}) returned success={stripped}.");

            if (stripped)
                this.alteredCharacters.Add(pending.GameObjectId);
        }
    }

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= this.Ui.Draw;

        this.emoteWatcher.EmoteMessageSeen -= this.OnEmoteMessageSeen;
        this.emoteWatcher.Dispose();

        this.emoteHook.LocalPlayerEmoteExecuted -= this.OnLocalPlayerEmoteExecuted;
        this.emoteHook.Dispose();
    }
}
