using System;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Flash;

/// <summary>
/// Mirrors the subset of Glamourer's ApplyFlag enum we need.
/// Glamourer's actual enum has more members (Customization, Lock, etc.) - add as needed.
/// Keep this in sync with whatever Glamourer version you're targeting; the IPC contract
/// is documented in Glamourer's repo under IPC.md.
/// </summary>
[Flags]
public enum ApplyFlag : uint
{
    Equipment = 1 << 0,
    Once = 1 << 4,
}

/// <summary>
/// Glamourer's IPC error codes (subset). 0 = Success.
/// </summary>
public enum GlamourerApiEc
{
    Success = 0,
    ActorNotFound = 1,
    Unknown = 255,
}

/// <summary>
/// Wraps Glamourer's ICallGate IPC endpoints. Glamourer must be installed and running
/// for any of these calls to succeed - always check ApiVersion or wrap calls in try/catch,
/// since a missing IPC subscriber throws IpcNotReadyError.
///
/// NOTE: this uses ICharacter (the public interface) rather than the concrete
/// Dalamud.Game.ClientState.Objects.Types.Character class, because that class is
/// internal in current Dalamud and can't be referenced from plugin code at all.
/// This assumes Glamourer's own IPC signature also takes ICharacter now. If IPC calls
/// throw an IpcTypeMismatchError at runtime, open Glamourer's Api/IpcSubscribers folder
/// (or IPC.md in its repo) for the exact current parameter type and adjust the generic
/// arguments on applyDesign/revertState below to match - it may instead expect a raw
/// game object pointer/address (nint) rather than ICharacter.
/// </summary>
public class GlamourerIpc
{
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<string, ICharacter?, uint, ApplyFlag, GlamourerApiEc> applyDesign;
    private readonly ICallGateSubscriber<ICharacter?, uint, ApplyFlag, GlamourerApiEc> revertState;

    private readonly IPluginLog log;

    public GlamourerIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;

        this.apiVersion = pi.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersions");
        this.applyDesign = pi.GetIpcSubscriber<string, ICharacter?, uint, ApplyFlag, GlamourerApiEc>("Glamourer.ApplyDesign");
        this.revertState = pi.GetIpcSubscriber<ICharacter?, uint, ApplyFlag, GlamourerApiEc>("Glamourer.RevertState");
    }

    /// <summary>Returns false if Glamourer isn't installed/loaded or the IPC isn't ready yet.</summary>
    public bool IsAvailable()
    {
        try
        {
            var (major, _) = this.apiVersion.InvokeFunc();
            return major >= 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Applies a base64-encoded Glamourer design string to the given actor.</summary>
    public bool ApplyDesign(string base64Design, ICharacter target, uint lockKey = 0)
    {
        if (string.IsNullOrWhiteSpace(base64Design))
            return false;

        try
        {
            var result = this.applyDesign.InvokeFunc(
                base64Design,
                target,
                lockKey,
                ApplyFlag.Once | ApplyFlag.Equipment);

            if (result != GlamourerApiEc.Success)
            {
                this.log.Warning($"[Flash] Glamourer.ApplyDesign returned {result}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Failed to call Glamourer.ApplyDesign");
            return false;
        }
    }

    /// <summary>Reverts the given actor back to their real equipped gear.</summary>
    public bool Revert(ICharacter target, uint lockKey = 0)
    {
        try
        {
            var result = this.revertState.InvokeFunc(target, lockKey, ApplyFlag.Once);
            if (result != GlamourerApiEc.Success)
            {
                this.log.Warning($"[Flash] Glamourer.RevertState returned {result}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Failed to call Glamourer.RevertState");
            return false;
        }
    }
}
