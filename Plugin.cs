using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaEmote = Lumina.Excel.Sheets.Emote;

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
    public DebugLogUi DebugLogWindow { get; }

    /// <summary>Rolling log of every locally-detected emote with its exact ID and resolved
    /// name, for finding IDs to enter in the manual override field. Always recording -
    /// this is a standalone tool, not tied to any toggle.</summary>
    public List<DebugLogEntry> DebugLog { get; } = new();

    public const int MaxDebugLogEntries = 200;

    public readonly record struct DebugLogEntry(DateTime Time, string Source, uint Id, string Name, bool Matched);

    private readonly EmoteWatcher emoteWatcher;
    private readonly EmoteHook emoteHook;
    private readonly ActionHook actionHook;

    // Emotes waiting out their trigger delay before gear gets stripped/swapped.
    private readonly List<PendingStrip> pendingStrips = new();

    // Characters currently wearing Flash-altered gear, mapped to which trigger type
    // caused it. Emote-triggered gear reverts via animation-end polling (see
    // ProcessAlteredCharacters); Action-triggered gear has no animation state to poll,
    // so it relies entirely on ProcessForcedReverts (Duration) instead - see
    // ProcessPendingStrips, which forces UseDuration on for Action entries regardless of
    // the checkbox, since otherwise it would never revert.
    private readonly Dictionary<ulong, TriggerType> alteredCharacters = new();

    // Scheduled forced reverts for entries with UseDuration=true - an early cutoff on
    // top of the normal animation-end detection, not a replacement for it.
    private readonly List<PendingForcedRevert> pendingForcedReverts = new();

    // Full Glamourer state (not just gear) captured right before stripping, so it can be
    // restored exactly instead of falling back to the character's real equipped gear -
    // see RevertCharacterGear. Keyed by GameObjectId, cleared once restored/consumed.
    private readonly Dictionary<ulong, string> savedGlamourerStates = new();

    private const string CommandName = "/flash";

    private readonly record struct PendingStrip(ulong GameObjectId, EmoteGearEntry Entry, DateTime StripAt);
    private readonly record struct PendingForcedRevert(ulong GameObjectId, DateTime RevertAt);

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

        this.actionHook = new ActionHook(GameInteropProvider, Log);
        this.actionHook.LocalPlayerActionUsed += this.OnLocalPlayerActionUsed;

        this.Ui = new PluginUi(this);
        PluginInterface.UiBuilder.Draw += this.Ui.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += () => this.Ui.IsOpen = true;

        this.DebugLogWindow = new DebugLogUi(this);
        PluginInterface.UiBuilder.Draw += this.DebugLogWindow.Draw;

        Framework.Update += this.OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the Flash config window. '/flash toggle' enables/disables the plugin. " +
                          "'/flash log' opens the Flash Debug Log for finding emote/action IDs. " +
                          "'/flash dumpstate' (temporary diagnostic) logs your decoded Glamourer state to /xllog.",
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

        if (string.Equals(trimmed, "log", StringComparison.OrdinalIgnoreCase))
        {
            this.DebugLogWindow.IsOpen = !this.DebugLogWindow.IsOpen;
            return;
        }

        if (string.Equals(trimmed, "dumpstate", StringComparison.OrdinalIgnoreCase))
        {
            this.DumpLocalPlayerState();
            return;
        }

        this.Ui.IsOpen = !this.Ui.IsOpen;
    }

    /// <summary>
    /// TEMPORARY DIAGNOSTIC - captures the local player's current Glamourer state and
    /// logs its decoded content to /xllog, so the real JSON schema can be confirmed
    /// instead of guessed. See GlamourerIpc.DecodeStateForDiagnostics. Remove once the
    /// schema is known and the real per-slot extractor is written.
    /// </summary>
    private void DumpLocalPlayerState()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            ChatGui.Print("[Flash] No local player found.");
            return;
        }

        var state = this.Glamourer.CaptureState(localPlayer);
        if (state == null)
        {
            ChatGui.Print("[Flash] CaptureState failed - check /xllog for the warning/error.");
            return;
        }

        var decoded = GlamourerIpc.DecodeStateForDiagnostics(state);
        Log.Information($"[Flash] Decoded Glamourer state:\n{decoded}");
        ChatGui.Print("[Flash] Decoded state dumped to /xllog - search for 'Decoded Glamourer state'.");
    }

    /// <summary>
    /// Called from EmoteHook whenever the local player executes any emote - native hook,
    /// exact numeric EmoteId, no dependency on chat settings. This is the primary/
    /// preferred detection path for local-player entries.
    /// </summary>
    private void OnLocalPlayerEmoteExecuted(ushort emoteId)
    {
        var match = this.FindMatchById(emoteId);
        this.AddDebugLogEntry("Emote", emoteId, ResolveEmoteName(emoteId), match != null);

        if (!this.Configuration.PluginEnabled)
            return;

        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        this.HandleEmoteForCharacter(match, localPlayer);
    }

    /// <summary>
    /// Called from ActionHook whenever the local player uses any action - native hook,
    /// exact numeric ActionId, fires the instant the game accepts the action (see the
    /// caveat on ActionHook's UseAction hook about queued actions).
    /// </summary>
    private void OnLocalPlayerActionUsed(uint actionId)
    {
        var match = this.FindMatchByActionId(actionId);
        this.AddDebugLogEntry("Action", actionId, ResolveActionName(actionId), match != null);

        if (!this.Configuration.PluginEnabled)
            return;

        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

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
        if (!this.Configuration.PluginEnabled)
            return;

        var localPlayer = ObjectTable.LocalPlayer;

        // Self-performed emotes typically render without a separate sender name (the
        // "You " is baked into the message text itself), so an empty sender means it's
        // very likely the local player. Otherwise compare names directly.
        var isLocalPlayer = string.IsNullOrEmpty(senderName)
            || (localPlayer != null && string.Equals(senderName, localPlayer.Name.TextValue, StringComparison.Ordinal));

        if (isLocalPlayer)
            return;

        var target = this.FindCharacterByName(senderName);
        if (target == null)
            return;

        var match = this.FindMatchByText(messageText);
        this.HandleEmoteForCharacter(match, target);
    }

    private static string ResolveEmoteName(uint id)
    {
        var sheet = DataManager.GetExcelSheet<LuminaEmote>();
        var row = sheet?.GetRowOrDefault(id);
        var name = row?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? "(unknown)" : name;
    }

    private static string ResolveActionName(uint id)
    {
        var sheet = DataManager.GetExcelSheet<LuminaAction>();
        var row = sheet?.GetRowOrDefault(id);
        var name = row?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? "(unknown)" : name;
    }

    private void AddDebugLogEntry(string source, uint id, string name, bool matched)
    {
        this.DebugLog.Add(new DebugLogEntry(DateTime.UtcNow, source, id, name, matched));

        if (this.DebugLog.Count > MaxDebugLogEntries)
            this.DebugLog.RemoveAt(0);
    }

    private EmoteGearEntry? FindMatchById(ushort emoteId)
    {
        foreach (var entry in this.Configuration.Entries)
        {
            if (entry.Enabled && entry.TriggerType == TriggerType.Emote && entry.EmoteId == emoteId)
                return entry;
        }

        return null;
    }

    private EmoteGearEntry? FindMatchByActionId(uint actionId)
    {
        foreach (var entry in this.Configuration.Entries)
        {
            if (entry.Enabled && entry.TriggerType == TriggerType.Action && entry.ActionId == actionId)
                return entry;
        }

        return null;
    }

    private EmoteGearEntry? FindMatchByText(string messageText)
    {
        foreach (var entry in this.Configuration.Entries)
        {
            if (entry.Enabled
                && entry.TriggerType == TriggerType.Emote
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
    /// null) matched entry are known. If matched, schedules a strip. If not matched:
    /// only an Emote-triggered alteration reverts immediately here (switching to a
    /// different, unmapped emote/animation cancels the effect). An Action-triggered
    /// alteration is left alone and relies entirely on its Duration timer
    /// (ProcessForcedReverts) instead - ActionHook fires for every action the player
    /// uses, so reverting on any unmatched one would mean the very next auto-attack or
    /// GCD cancels the effect almost immediately, defeating Duration entirely. A
    /// still-pending strip for this character from a previous trigger is always
    /// cancelled first, so a rapid change can't apply a stale strip after the fact.
    /// </summary>
    private void HandleEmoteForCharacter(EmoteGearEntry? match, ICharacter target)
    {
        this.pendingStrips.RemoveAll(p => p.GameObjectId == target.GameObjectId);

        if (match != null)
        {
            this.pendingForcedReverts.RemoveAll(p => p.GameObjectId == target.GameObjectId);
            this.pendingStrips.Add(new PendingStrip(
                target.GameObjectId,
                match,
                DateTime.UtcNow.AddSeconds(match.TriggerDelaySeconds)));

            return;
        }

        if (this.alteredCharacters.TryGetValue(target.GameObjectId, out var currentTriggerType)
            && currentTriggerType == TriggerType.Emote)
        {
            this.alteredCharacters.Remove(target.GameObjectId);
            this.pendingForcedReverts.RemoveAll(p => p.GameObjectId == target.GameObjectId);
            this.RevertCharacterGear(target, target.GameObjectId);
        }
    }

    /// <summary>
    /// Restores whatever Glamourer state was captured right before Flash stripped this
    /// character. Three-tier fallback: RestoreGearFromState (reads each slot's ItemId/
    /// Stain straight from the captured JSON, writes it back via SetItem - never touches
    /// ApplyState/RevertState, so it never disturbs customization and never triggers
    /// Glamourer's redraw) is tried first and should succeed in normal operation; if it
    /// can't even decode/parse the captured state, falls back to RestoreState (full
    /// ApplyState apply - does redraw, but keeps correctness); if that also fails, falls
    /// back to a plain Revert (real equipped gear, no overrides preserved).
    /// </summary>
    private void RevertCharacterGear(ICharacter character, ulong gameObjectId)
    {
        if (this.savedGlamourerStates.Remove(gameObjectId, out var savedState))
        {
            if (this.Glamourer.RestoreGearFromState(character, savedState, this.Configuration.EnabledSlots))
                return;

            if (this.Glamourer.RestoreState(character, savedState))
                return;
        }

        this.Glamourer.Revert(character);
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
        // Check animation-ended characters first (using state from before this tick's
        // new strips are applied), so a character stripped this very frame isn't
        // immediately re-checked and potentially reverted before the animation has had
        // a chance to register as playing.
        this.ProcessAlteredCharacters();
        this.ProcessForcedReverts();
        this.ProcessPendingStrips();
    }

    /// <summary>
    /// Polls every Emote-triggered character with Flash-altered gear and reverts them
    /// the moment their animation actually finishes (IsEmoting/IsInEmoteLoop both false)
    /// - covers both a single emote playing out naturally and a looping emote being
    /// cancelled by movement or re-triggering, without needing a timer. Action-triggered
    /// characters are skipped here entirely (see the field comment on
    /// alteredCharacters) - they have no animation state to poll and rely on
    /// ProcessForcedReverts instead.
    /// </summary>
    private void ProcessAlteredCharacters()
    {
        if (this.alteredCharacters.Count == 0)
            return;

        // Snapshot - Revert below can mutate alteredCharacters mid-iteration.
        foreach (var (gameObjectId, triggerType) in new Dictionary<ulong, TriggerType>(this.alteredCharacters))
        {
            if (triggerType != TriggerType.Emote)
                continue;

            ICharacter? character = null;
            foreach (var obj in ObjectTable)
            {
                if (obj is ICharacter c && c.GameObjectId == gameObjectId)
                {
                    character = c;
                    break;
                }
            }

            if (character == null)
            {
                // Left the object table entirely (logged out, zoned, etc.) - nothing to
                // revert, just stop tracking them.
                this.alteredCharacters.Remove(gameObjectId);
                this.pendingForcedReverts.RemoveAll(p => p.GameObjectId == gameObjectId);
                this.savedGlamourerStates.Remove(gameObjectId);
                continue;
            }

            if (!NativeCharacterHelper.IsEmoting(character.Address))
            {
                this.alteredCharacters.Remove(gameObjectId);
                this.pendingForcedReverts.RemoveAll(p => p.GameObjectId == gameObjectId);
                this.RevertCharacterGear(character, gameObjectId);
            }
        }
    }

    /// <summary>
    /// Force-reverts any character whose entry has UseDuration=true once DurationSeconds
    /// has elapsed, even if the animation is still playing - lets a change be cut short
    /// deliberately instead of always waiting for the animation to end naturally.
    /// </summary>
    private void ProcessForcedReverts()
    {
        if (this.pendingForcedReverts.Count == 0)
            return;

        var now = DateTime.UtcNow;
        for (var i = this.pendingForcedReverts.Count - 1; i >= 0; i--)
        {
            var pending = this.pendingForcedReverts[i];
            if (pending.RevertAt > now)
                continue;

            this.pendingForcedReverts.RemoveAt(i);

            if (!this.alteredCharacters.Remove(pending.GameObjectId))
                continue; // Already reverted naturally - nothing to do.

            var foundCharacter = false;
            foreach (var obj in ObjectTable)
            {
                if (obj is ICharacter character && character.GameObjectId == pending.GameObjectId)
                {
                    foundCharacter = true;
                    this.RevertCharacterGear(character, pending.GameObjectId);
                    break;
                }
            }

            if (!foundCharacter)
                this.savedGlamourerStates.Remove(pending.GameObjectId);
        }
    }

    private void ProcessPendingStrips()
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
                continue;

            if (!this.Glamourer.IsAvailable())
            {
                Log.Warning("[Flash] Glamourer is not installed/loaded - cannot strip gear.");
                continue;
            }

            // Snapshot the character's current Glamourer state (any active gender/
            // customization/design override, not just gear) before stripping, so it can
            // be restored exactly later instead of falling back to their real equipped
            // gear/body - see RevertCharacterGear. Only do this if the character isn't
            // already altered - re-triggering the same (or another) mapped entry while
            // still stripped would otherwise overwrite the true "before" snapshot with
            // the current already-stripped state, so restoring later would just restore
            // "naked" instead of the original appearance.
            if (!this.alteredCharacters.ContainsKey(pending.GameObjectId))
            {
                var savedState = this.Glamourer.CaptureState(target);
                if (savedState != null)
                    this.savedGlamourerStates[pending.GameObjectId] = savedState;
            }

            var stripped = this.Glamourer.StripAllGear(target, this.Configuration.StripMode, this.Configuration.EnabledSlots);

            if (stripped)
            {
                this.alteredCharacters[pending.GameObjectId] = pending.Entry.TriggerType;

                // Action entries have no animation state to poll (see
                // ProcessAlteredCharacters), so Duration is their only way back to
                // normal - force it on regardless of the checkbox, using whatever
                // DurationSeconds is configured (or the field's own default), so an
                // Action mapping can never leave gear stuck permanently.
                var useDuration = pending.Entry.UseDuration || pending.Entry.TriggerType == TriggerType.Action;

                if (useDuration)
                {
                    this.pendingForcedReverts.Add(new PendingForcedRevert(
                        pending.GameObjectId,
                        DateTime.UtcNow.AddSeconds(pending.Entry.DurationSeconds)));
                }
            }
        }
    }

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= this.Ui.Draw;
        PluginInterface.UiBuilder.Draw -= this.DebugLogWindow.Draw;

        this.emoteWatcher.EmoteMessageSeen -= this.OnEmoteMessageSeen;
        this.emoteWatcher.Dispose();

        this.emoteHook.LocalPlayerEmoteExecuted -= this.OnLocalPlayerEmoteExecuted;
        this.emoteHook.Dispose();

        this.actionHook.LocalPlayerActionUsed -= this.OnLocalPlayerActionUsed;
        this.actionHook.Dispose();
    }
}
