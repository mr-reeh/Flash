using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Flash;

/// <summary>
/// Fired whenever the local player uses any action (weaponskill, spell, item, mount,
/// general action, etc.), with its numeric ActionId.
/// </summary>
public delegate void LocalPlayerActionUsedDelegate(uint actionId);

/// <summary>
/// Hooks FFXIVClientStructs' ActionManager.UseAction directly - the central entry point
/// for the local player using any action. Confirmed against the user's installed
/// FFXIVClientStructs.dll via IDE decompilation, not guessed:
///   public unsafe bool UseAction(ActionType actionType, uint actionId, ulong targetId = ...,
///       uint extraParam = 0u, UseActionMode mode = ..., uint comboRouteId = 0u,
///       bool* outOptAreaTargeted = null)
/// - an instance method on the ActionManager singleton, reached via ActionManager.Instance().
///
/// CAVEAT (from FFXIVClientStructs' own doc comment on UseAction): "If called shortly
/// before action is available (due to cooldown or animation lock), action is queued"
/// rather than executed immediately - so this can fire slightly before the actual
/// animation/effect, or for an action that ends up queued/cancelled. Only fires when the
/// underlying call returns true (accepted) to filter out obviously-rejected attempts,
/// which is what most combat-detection plugins do, but this isn't frame-perfect sync to
/// a specific VFX moment.
/// </summary>
public unsafe class ActionHook : IDisposable
{
    public event LocalPlayerActionUsedDelegate? LocalPlayerActionUsed;

    private readonly IPluginLog log;
    private readonly IGameInteropProvider hooking;

    private delegate bool UseActionDelegate(
        ActionManager* thisPtr,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted);

    private Hook<UseActionDelegate>? useActionHook;

    public ActionHook(IGameInteropProvider hooking, IPluginLog log)
    {
        this.hooking = hooking;
        this.log = log;

        try
        {
            this.useActionHook = this.hooking.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction,
                this.UseActionDetour);

            this.useActionHook.Enable();
            this.log.Information("[Flash] ActionManager.UseAction hook enabled.");
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Failed to hook ActionManager.UseAction - action-based triggers will not work this session.");
        }
    }

    private bool UseActionDetour(
        ActionManager* thisPtr,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        // Always call the original first so we never block normal action execution,
        // even if our own handling below throws.
        var result = this.useActionHook!.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

        try
        {
            if (result)
                this.LocalPlayerActionUsed?.Invoke(actionId);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Error in LocalPlayerActionUsed subscriber");
        }

        return result;
    }

    public void Dispose()
    {
        this.useActionHook?.Disable();
        this.useActionHook?.Dispose();
    }
}
