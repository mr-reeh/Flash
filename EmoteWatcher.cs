using System;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace Flash;

/// <summary>
/// Fired when a chat message matching a tracked emote-log chat type is seen.
/// </summary>
public delegate void EmoteMessageSeenDelegate(string senderName, string messageText);

/// <summary>
/// Detects emotes by listening to chat rather than hooking a native game function.
///
/// BACKGROUND: the original design hooked FFXIVClientStructs' EmoteManager.ExecuteEmote
/// directly for frame-perfect, EmoteId-based detection. That failed to build twice
/// against a current Dalamud/FFXIVClientStructs install: the old global EmoteManager
/// singleton has since been refactored into a per-Character EmoteController component,
/// and the exact current member function for triggering an emote could not be reliably
/// confirmed from public documentation/source snippets. Rather than guess a third time
/// at internals that are known to shift between patches, this watches chat instead.
///
/// FFXIV logs a line to chat whenever any character performs an emote - XivChatType.
/// StandardEmote for the built-in animations, XivChatType.CustomEmote for /em text -
/// as long as the client's "Log Emotes" setting (Character Configuration > Log Window
/// Settings) is enabled, which it is by default.
///
/// IMPLEMENTATION NOTE: IChatGui.ChatMessage's handler parameter is an
/// IHandleableChatMessage (Dalamud v15+). This class subscribes with an inline lambda
/// rather than a named method with an explicit parameter type, because the exact
/// namespace that interface lives in couldn't be confirmed from available
/// documentation/source - a lambda assigned directly to the event has its parameter
/// type inferred from the delegate, so it never needs to be named. If you want to
/// convert this to a named method later, right-click IChatGui.ChatMessage in your IDE
/// and "Go to Definition" to see the exact type and its namespace.
///
/// TRADE-OFF: this gives you the rendered chat text (e.g. "You wave.") rather than a
/// numeric EmoteId, so Plugin.cs matches a configured mapping via a case-insensitive
/// substring check against the emote's name, not an exact ID comparison. This is less
/// precise and is client-language-dependent.
/// </summary>
public class EmoteWatcher : IDisposable
{
    public event EmoteMessageSeenDelegate? EmoteMessageSeen;

    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly IChatGui.OnHandleableChatMessageDelegate handler;

    public EmoteWatcher(IChatGui chatGui, IPluginLog log)
    {
        this.chatGui = chatGui;
        this.log = log;

        this.handler = message =>
        {
            if (message.LogKind != XivChatType.StandardEmote && message.LogKind != XivChatType.CustomEmote)
                return;

            try
            {
                var senderName = message.Sender?.TextValue ?? string.Empty;
                var text = message.Message.TextValue;
                this.EmoteMessageSeen?.Invoke(senderName, text);
            }
            catch (Exception ex)
            {
                this.log.Error(ex, "[Flash] Error handling chat message in EmoteWatcher");
            }
        };

        this.chatGui.ChatMessage += this.handler;
    }

    public void Dispose()
    {
        this.chatGui.ChatMessage -= this.handler;
    }
}
