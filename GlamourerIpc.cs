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
/// CONFIDENCE NOTE: ApiVersion and RevertState mirror Glamourer's IPC pattern that was
/// directly confirmed against Glamourer's own source earlier in this project, so those
/// should be solid. SetItem's real signature - int objectIndex, ApiEquipSlot slot, ulong
/// itemId, IReadOnlyList&lt;StainId&gt; stains (a List&lt;byte&gt; client-side, not
/// byte[] - see StripAllGear), uint key, ApplyFlag flags - was confirmed via two live
/// runtime errors during testing: an empty stains array crashed Glamourer's own
/// StainIds constructor, and a byte[] (vs. List&lt;byte&gt;) tripped Dalamud's IPC JSON
/// round-trip because Newtonsoft serializes byte[] as base64 instead of a JSON array.
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
    /// Attempts every slot even after a failure, so one bad slot doesn't leave the
    /// character half-dressed. Pass <paramref name="onSlotResult"/> to get a line per
    /// slot (e.g. to echo to chat in debug mode) instead of only the aggregate result.
    /// </summary>
    public bool StripAllGear(ICharacter target, uint lockKey = 0, Action<string>? onSlotResult = null)
    {
        var allSucceeded = true;

        foreach (var slot in StrippableSlots)
        {
            try
            {
                // ItemId 0 represents an empty/unequipped slot. Stains must be a
                // List<byte>, not a byte[] - Dalamud's cross-plugin IPC round-trips
                // mismatched argument types through JSON, and Newtonsoft special-cases
                // byte[] as a base64 string (confirmed via a live IpcTypeMismatchError),
                // which then fails to deserialize into IReadOnlyList<byte>. List<byte>
                // serializes as a normal JSON array and deserializes correctly.
                var result = this.setItem.Invoke(target.ObjectIndex, slot, 0, new List<byte> { 0, 0 }, lockKey);

                if (result != GlamourerApiEc.Success && result != GlamourerApiEc.NothingDone)
                {
                    this.log.Warning($"[Flash] Glamourer.SetItem({slot}) returned {result}");
                    onSlotResult?.Invoke($"{slot}: FAILED ({result})");
                    allSucceeded = false;
                }
                else
                {
                    onSlotResult?.Invoke($"{slot}: ok ({result})");
                }
            }
            catch (Exception ex)
            {
                this.log.Error(ex, $"[Flash] Failed to call Glamourer.SetItem for slot {slot}");
                onSlotResult?.Invoke($"{slot}: EXCEPTION ({ex.GetType().Name}: {ex.Message})");
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
