using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace Flash;

/// <summary>
/// Fired whenever the local player executes an emote (via /emote, the emote wheel, or a
/// macro), with its numeric EmoteId. Unlike chat-based detection, this does not depend on
/// the client's "Log Emotes" setting.
/// </summary>
public delegate void LocalPlayerEmoteExecutedDelegate(ushort emoteId);

/// <summary>
/// Hooks FFXIVClientStructs' EmoteManager.ExecuteEmote directly instead of relying on
/// chat log lines. Per FFXIVClientStructs' own doc comment on EmoteController.PlayEmote,
/// EmoteManager.ExecuteEmote is specifically the function used "for the local player"
/// (other characters go through EmoteController.PlayEmote on their own instance instead -
/// this class does NOT hook that; remote-player detection still goes through
/// EmoteWatcher's chat parsing).
///
/// This signature was confirmed directly against the user's installed
/// FFXIVClientStructs.dll via their IDE's decompiler (not guessed from search):
///   public unsafe bool ExecuteEmote(ushort emoteId, EmoteController.PlayEmoteOption* playEmoteOption = null)
/// - an instance method on the EmoteManager singleton, reached via EmoteManager.Instance().
/// </summary>
public unsafe class EmoteHook : IDisposable
{
    public event LocalPlayerEmoteExecutedDelegate? LocalPlayerEmoteExecuted;

    private readonly IPluginLog log;
    private readonly IGameInteropProvider hooking;

    private delegate bool ExecuteEmoteDelegate(EmoteManager* thisPtr, ushort emoteId, EmoteController.PlayEmoteOption* playEmoteOption);

    private Hook<ExecuteEmoteDelegate>? executeEmoteHook;

    public EmoteHook(IGameInteropProvider hooking, IPluginLog log)
    {
        this.hooking = hooking;
        this.log = log;

        try
        {
            this.executeEmoteHook = this.hooking.HookFromAddress<ExecuteEmoteDelegate>(
                (nint)EmoteManager.MemberFunctionPointers.ExecuteEmote,
                this.ExecuteEmoteDetour);

            this.executeEmoteHook.Enable();
            this.log.Information("[Flash] EmoteManager.ExecuteEmote hook enabled.");
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Failed to hook EmoteManager.ExecuteEmote - native emote detection for " +
                                "the local player will not work this session; chat-based detection (EmoteWatcher) " +
                                "still applies for the local player as a fallback, if 'Log Emotes' is on.");
        }
    }

    private bool ExecuteEmoteDetour(EmoteManager* thisPtr, ushort emoteId, EmoteController.PlayEmoteOption* playEmoteOption)
    {
        // Always call the original first so we never block normal emote execution,
        // even if our own handling below throws.
        var result = this.executeEmoteHook!.Original(thisPtr, emoteId, playEmoteOption);

        try
        {
            this.LocalPlayerEmoteExecuted?.Invoke(emoteId);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Error in LocalPlayerEmoteExecuted subscriber");
        }

        return result;
    }

    public void Dispose()
    {
        this.executeEmoteHook?.Disable();
        this.executeEmoteHook?.Dispose();
    }
}
