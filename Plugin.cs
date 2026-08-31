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

    public Configuration Configuration { get; }
    public GlamourerIpc Glamourer { get; }
    public PluginUi Ui { get; }

    private readonly EmoteWatcher emoteWatcher;

    // Tracks characters we've applied a temporary design to, so we can revert them later.
    private readonly List<PendingRevert> pendingReverts = new();

    private const string CommandName = "/emotegear";

    private readonly record struct PendingRevert(ulong GameObjectId, DateTime RevertAt);

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);

        this.Glamourer = new GlamourerIpc(PluginInterface, Log);
        this.emoteWatcher = new EmoteWatcher(ChatGui, Log);
        this.emoteWatcher.EmoteMessageSeen += this.OnEmoteMessageSeen;

        this.Ui = new PluginUi(this);
        PluginInterface.UiBuilder.Draw += this.Ui.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += () => this.Ui.IsOpen = true;

        Framework.Update += this.OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the emote gear config window. Use '/emotegear toggle' to enable/disable the plugin.",
        });
    }

    private void OnCommand(string command, string args)
    {
        if (string.Equals(args.Trim(), "toggle", StringComparison.OrdinalIgnoreCase))
        {
            this.Configuration.PluginEnabled = !this.Configuration.PluginEnabled;
            this.Configuration.Save();
            Log.Information($"[Flash] Plugin {(this.Configuration.PluginEnabled ? "enabled" : "disabled")}.");
            return;
        }

        this.Ui.IsOpen = !this.Ui.IsOpen;
    }

    /// <summary>
    /// Called from EmoteWatcher whenever a StandardEmote/CustomEmote line appears in
    /// chat. Fires on the framework thread (Dalamud invokes chat callbacks there), so
    /// touching ObjectTable/Glamourer IPC directly here is safe - no extra marshaling
    /// needed.
    ///
    /// Matching is a case-insensitive substring check of the configured emote's name
    /// against the rendered chat text (see EmoteWatcher.cs for why - this doesn't get a
    /// numeric EmoteId like the original native-hook design would have).
    /// </summary>
    private void OnEmoteMessageSeen(string senderName, string messageText)
    {
        if (!this.Configuration.PluginEnabled)
            return;

        EmoteGearEntry? match = null;
        foreach (var entry in this.Configuration.Entries)
        {
            if (entry.Enabled
                && !string.IsNullOrWhiteSpace(entry.EmoteName)
                && messageText.Contains(entry.EmoteName, StringComparison.OrdinalIgnoreCase))
            {
                match = entry;
                break;
            }
        }

        if (match == null)
            return;

        var localPlayer = ObjectTable.LocalPlayer;

        // Self-performed emotes typically render without a separate sender name (the
        // "You " is baked into the message text itself), so an empty sender means it's
        // very likely the local player. Otherwise compare names directly.
        var isLocalPlayer = string.IsNullOrEmpty(senderName)
            || (localPlayer != null && string.Equals(senderName, localPlayer.Name.TextValue, StringComparison.Ordinal));

        if (match.LocalPlayerOnly && !isLocalPlayer)
            return;

        var target = isLocalPlayer ? localPlayer : this.FindCharacterByName(senderName);
        if (target == null)
            return;

        if (!this.Glamourer.IsAvailable())
        {
            Log.Warning("[Flash] Glamourer is not installed/loaded - cannot apply design.");
            return;
        }

        var applied = this.Glamourer.ApplyDesign(match.GlamourerDesignBase64, target);
        if (applied && match.RevertAfterEmote)
        {
            this.pendingReverts.Add(new PendingRevert(
                target.GameObjectId,
                DateTime.UtcNow.AddSeconds(match.RevertDelaySeconds)));
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
        if (this.pendingReverts.Count == 0)
            return;

        var now = DateTime.UtcNow;
        for (var i = this.pendingReverts.Count - 1; i >= 0; i--)
        {
            var pending = this.pendingReverts[i];
            if (pending.RevertAt > now)
                continue;

            this.pendingReverts.RemoveAt(i);

            foreach (var obj in ObjectTable)
            {
                if (obj is ICharacter character && character.GameObjectId == pending.GameObjectId)
                {
                    this.Glamourer.Revert(character);
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= this.Ui.Draw;

        this.emoteWatcher.EmoteMessageSeen -= this.OnEmoteMessageSeen;
        this.emoteWatcher.Dispose();
    }
}
