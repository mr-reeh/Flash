using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;

namespace Flash;

/// <summary>
/// Wraps the Glamourer.Api NuGet package (compile-time-checked IPC types) rather than
/// hand-rolled ICallGateSubscriber calls with guessed generic signatures.
///
/// CONFIDENCE NOTE: ApiVersion and RevertState below mirror Glamourer's IPC pattern that
/// was directly confirmed against Glamourer's own source earlier in this project (the
/// "Glamourer.ApiVersions" / "Glamourer.RevertState" IPC names), so those should be solid.
/// SetItem (used for stripping gear) is constructed from the Glamourer.Api package's
/// naming conventions and a source diff showing how Glamourer's own IpcProviders wire it
/// up server-side, but the exact client-side Invoke(...) parameter order/types could not
/// be independently confirmed from outside the package. If this doesn't compile, the
/// compiler error will name the real expected parameter types directly from the
/// Glamourer.Api assembly - paste that error back and it's a one-line fix, not more
/// guessing.
/// </summary>
public class GlamourerIpc
{
    private readonly IPluginLog log;

    private readonly ApiVersion apiVersion;
    private readonly SetItem setItem;
    private readonly RevertState revertState;

    /// <summary>
    /// The equipment slots this plugin strips. Deliberately excludes MainHand/OffHand
    /// (weapons) since the request was for armor/accessory slots only.
    /// </summary>
    public static readonly IReadOnlyList<ApiEquipSlot> StrippableSlots = new[]
    {
        ApiEquipSlot.Head,
        ApiEquipSlot.Body,
        ApiEquipSlot.Hands,
        ApiEquipSlot.Legs,
        ApiEquipSlot.Feet,
        ApiEquipSlot.Ears,
        ApiEquipSlot.Neck,
        ApiEquipSlot.Wrists,
        ApiEquipSlot.RFinger,
        ApiEquipSlot.LFinger,
    };

    public GlamourerIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;

        this.apiVersion = new ApiVersion(pi);
        this.setItem = new SetItem(pi);
        this.revertState = new RevertState(pi);
    }

    /// <summary>Returns false if Glamourer isn't installed/loaded or the IPC isn't ready yet.</summary>
    public bool IsAvailable()
    {
        try
        {
            var (major, _) = this.apiVersion.Invoke();
            return major >= 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Sets every slot in <see cref="StrippableSlots"/> to "nothing" on the given actor.
    /// Returns false (and logs) on the first slot that fails, but still attempts the
    /// remaining slots so a single bad slot doesn't leave the character half-dressed.
    /// </summary>
    public bool StripAllGear(ICharacter target, uint lockKey = 0)
    {
        var allSucceeded = true;

        foreach (var slot in StrippableSlots)
        {
            try
            {
                // ItemId 0 represents an empty/unequipped slot. If this throws an
                // IpcTypeMismatchError or similar at runtime instead of failing to
                // compile, the parameter types below don't match Glamourer's actual
                // SetItem signature - check Glamourer.Api's SetItem.cs (via "Go to
                // Definition" in your IDE) for the real one and adjust this call.
                var result = this.setItem.Invoke(target.ObjectIndex, slot, 0, Array.Empty<byte>(), lockKey);

                if (result != GlamourerApiEc.Success && result != GlamourerApiEc.NothingDone)
                {
                    this.log.Warning($"[Flash] Glamourer.SetItem({slot}) returned {result}");
                    allSucceeded = false;
                }
            }
            catch (Exception ex)
            {
                this.log.Error(ex, $"[Flash] Failed to call Glamourer.SetItem for slot {slot}");
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    /// <summary>Reverts the given actor back to their real equipped gear.</summary>
    public bool Revert(ICharacter target, uint lockKey = 0)
    {
        try
        {
            var result = this.revertState.Invoke(target.ObjectIndex, lockKey);
            if (result != GlamourerApiEc.Success && result != GlamourerApiEc.NothingDone)
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
