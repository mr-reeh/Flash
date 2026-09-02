using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Lumina.Excel.Sheets;

namespace Flash;

/// <summary>
/// Wraps the Glamourer.Api NuGet package (compile-time-checked IPC types) rather than
/// hand-rolled ICallGateSubscriber calls with guessed generic signatures.
///
/// CONFIDENCE NOTE: ApiVersion mirrors Glamourer's IPC pattern that was directly
/// confirmed against Glamourer's own source earlier in this project. SetItem's real
/// signature - int objectIndex, ApiEquipSlot slot, ulong itemId, IReadOnlyList&lt;StainId&gt;
/// stains (a List&lt;byte&gt; client-side, not byte[] - see StripAllGear), uint key,
/// ApplyFlag flags - was confirmed via two live runtime errors during testing: an empty
/// stains array crashed Glamourer's own StainIds constructor, and a byte[] (vs.
/// List&lt;byte&gt;) tripped Dalamud's IPC JSON round-trip because Newtonsoft serializes
/// byte[] as base64 instead of a JSON array.
///
/// RestoreCustomization deliberately never passes ApplyFlag.Equipment - an earlier
/// version restored the whole captured state (Equipment | Customization) via ApplyState,
/// which worked for preserving things like a gender swap through a strip/revert cycle,
/// but also reapplied the ENTIRE equipped-items block including weapons as a side
/// effect, causing an unwanted weapon redraw/VFX reset on every revert. Gear itself is
/// now restored separately via NativeInventoryHelper + SetItem (see
/// Plugin.RevertCharacterGear), which reads/writes only the specific armor/accessory
/// slots Flash actually touched and never references weapon slots at all.
/// </summary>
public class GlamourerIpc
{
    private readonly IPluginLog log;

    private readonly ApiVersion apiVersion;
    private readonly SetItem setItem;
    private readonly GetStateBase64 getStateBase64;
    private readonly ApplyState applyState;

    private Dictionary<ApiEquipSlot, uint>? emperorsSetItemIds;

    /// <summary>
    /// The equipment slots this plugin can strip/swap. Deliberately excludes MainHand/
    /// OffHand (weapons) - Flash never reads or writes weapon slots anywhere. Which of
    /// these are actually touched on a given strip is controlled by
    /// Configuration.EnabledSlots, not this list - this is the master set used to build
    /// the checkbox UI and as the default when EnabledSlots isn't yet configured.
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

    /// <summary>User-facing names for each slot in <see cref="StrippableSlots"/>, for the
    /// per-slot checkboxes in the config UI.</summary>
    public static readonly IReadOnlyDictionary<ApiEquipSlot, string> SlotDisplayNames = new Dictionary<ApiEquipSlot, string>
    {
        [ApiEquipSlot.Head] = "Head",
        [ApiEquipSlot.Body] = "Body",
        [ApiEquipSlot.Hands] = "Hands",
        [ApiEquipSlot.Legs] = "Legs",
        [ApiEquipSlot.Feet] = "Feet",
        [ApiEquipSlot.Ears] = "Earrings",
        [ApiEquipSlot.Neck] = "Necklace",
        [ApiEquipSlot.Wrists] = "Bracelet",
        [ApiEquipSlot.RFinger] = "Right Ring",
        [ApiEquipSlot.LFinger] = "Left Ring",
    };

    // Item names as they appear in Lumina's Item sheet. Resolved to real ItemIds at
    // runtime (see ResolveEmperorsSetItemIds) rather than hardcoded, since a wrong
    // hardcoded ID would silently equip the wrong item with no compile-time signal.
    private static readonly IReadOnlyDictionary<ApiEquipSlot, string> EmperorsSetItemNames = new Dictionary<ApiEquipSlot, string>
    {
        [ApiEquipSlot.Head] = "The Emperor's New Hat",
        [ApiEquipSlot.Body] = "The Emperor's New Robe",
        [ApiEquipSlot.Hands] = "The Emperor's New Gloves",
        [ApiEquipSlot.Legs] = "The Emperor's New Breeches",
        [ApiEquipSlot.Feet] = "The Emperor's New Boots",
        [ApiEquipSlot.Ears] = "The Emperor's New Earrings",
        [ApiEquipSlot.Neck] = "The Emperor's New Necklace",
        [ApiEquipSlot.Wrists] = "The Emperor's New Bracelet",
        [ApiEquipSlot.RFinger] = "The Emperor's New Ring",
        [ApiEquipSlot.LFinger] = "The Emperor's New Ring",
    };

    public GlamourerIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;

        this.apiVersion = new ApiVersion(pi);
        this.setItem = new SetItem(pi);
        this.getStateBase64 = new GetStateBase64(pi);
        this.applyState = new ApplyState(pi);
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
    /// Looks up each Emperor's New Set item by name in Lumina's Item sheet and caches
    /// the resulting ItemIds. Call once at plugin startup (DataManager is ready by then).
    /// Safe to call more than once; only does the sheet scan the first time.
    /// </summary>
    public void ResolveEmperorsSetItemIds(IDataManager dataManager)
    {
        if (this.emperorsSetItemIds != null)
            return;

        var sheet = dataManager.GetExcelSheet<Item>();
        if (sheet == null)
        {
            this.log.Warning("[Flash] Could not load the Item sheet - Emperor's Set mode won't have any items to equip.");
            this.emperorsSetItemIds = new Dictionary<ApiEquipSlot, uint>();
            return;
        }

        var resolved = new Dictionary<ApiEquipSlot, uint>();
        foreach (var (slot, itemName) in EmperorsSetItemNames)
        {
            var row = sheet.FirstOrDefault(item => string.Equals(item.Name.ExtractText(), itemName, StringComparison.OrdinalIgnoreCase));
            if (row.RowId != 0)
            {
                resolved[slot] = row.RowId;
            }
            else
            {
                this.log.Warning($"[Flash] Could not find item '{itemName}' in the Item sheet - {slot} will be skipped in Emperor's Set mode.");
            }
        }

        this.emperorsSetItemIds = resolved;
        this.log.Information($"[Flash] Resolved {resolved.Count}/{EmperorsSetItemNames.Count} Emperor's Set items.");
    }

    /// <summary>
    /// Sets every slot in <paramref name="slots"/> according to <paramref name="mode"/> -
    /// item 0 ("nothing") for Smallclothes, or the resolved Emperor's Set item for
    /// EmperorsSet. Attempts every slot even after a failure, so one bad slot doesn't
    /// leave the character half-dressed. Pass <paramref name="onSlotResult"/> to get a
    /// line per slot (e.g. to echo to chat in debug mode) instead of only the aggregate
    /// result.
    /// </summary>
    public bool StripAllGear(ICharacter target, StripMode mode, IEnumerable<ApiEquipSlot> slots, uint lockKey = 0, Action<string>? onSlotResult = null)
    {
        var allSucceeded = true;

        foreach (var slot in slots)
        {
            ulong itemId = 0;
            if (mode == StripMode.EmperorsSet
                && this.emperorsSetItemIds != null
                && this.emperorsSetItemIds.TryGetValue(slot, out var resolvedId))
            {
                itemId = resolvedId;
            }

            try
            {
                // ItemId 0 represents an empty/unequipped slot. Stains must be a
                // List<byte>, not a byte[] - Dalamud's cross-plugin IPC round-trips
                // mismatched argument types through JSON, and Newtonsoft special-cases
                // byte[] as a base64 string (confirmed via a live IpcTypeMismatchError),
                // which then fails to deserialize into IReadOnlyList<byte>. List<byte>
                // serializes as a normal JSON array and deserializes correctly.
                var result = this.setItem.Invoke(target.ObjectIndex, slot, itemId, new List<byte> { 0, 0 }, lockKey);

                if (result != GlamourerApiEc.Success && result != GlamourerApiEc.NothingDone)
                {
                    this.log.Warning($"[Flash] Glamourer.SetItem({slot}) returned {result}");
                    onSlotResult?.Invoke($"{slot}: FAILED ({result})");
                    allSucceeded = false;
                }
                else
                {
                    onSlotResult?.Invoke($"{slot}: ok ({result}, item {itemId})");
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

    /// <summary>
    /// Captures the character's full current Glamourer state (any active design/
    /// customization/gender override, not just gear) as an opaque string, so the
    /// customization portion can be restored later via RestoreCustomization.
    /// Returns null on failure.
    /// </summary>
    public string? CaptureState(ICharacter target, uint lockKey = 0)
    {
        try
        {
            var (ec, state) = this.getStateBase64.Invoke(target.ObjectIndex, lockKey);
            if (ec != GlamourerApiEc.Success || string.IsNullOrEmpty(state))
            {
                this.log.Warning($"[Flash] Glamourer.GetStateBase64 returned {ec}");
                return null;
            }

            return state;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Failed to call Glamourer.GetStateBase64");
            return null;
        }
    }

    /// <summary>
    /// Restores only the customization (body/gender/face) portion of a state string
    /// previously captured via CaptureState - deliberately never passes
    /// ApplyFlag.Equipment, so this cannot touch gear or weapons at all. Gear is
    /// restored separately by RestoreGearSlots.
    /// </summary>
    public bool RestoreCustomization(ICharacter target, string state, uint lockKey = 0)
    {
        try
        {
            var result = this.applyState.Invoke(
                state,
                target.ObjectIndex,
                lockKey,
                ApplyFlag.Once | ApplyFlag.Customization);

            if (result != GlamourerApiEc.Success && result != GlamourerApiEc.NothingDone)
            {
                this.log.Warning($"[Flash] Glamourer.ApplyState (customization) returned {result}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Failed to call Glamourer.ApplyState (customization)");
            return false;
        }
    }

    /// <summary>
    /// Restores each of <paramref name="slots"/> to the character's REAL equipped item
    /// (read directly from game memory via NativeInventoryHelper, bypassing Glamourer),
    /// then writes it back via SetItem. Never references weapon slots - slots is always
    /// a subset of StrippableSlots. Trade-off: this restores the actual equipped item,
    /// not a Glamourer-overridden appearance for that specific slot (see the note on
    /// NativeInventoryHelper.TryGetEquippedItem).
    /// </summary>
    public bool RestoreGearSlots(ICharacter target, IEnumerable<ApiEquipSlot> slots, uint lockKey = 0, Action<string>? onSlotResult = null)
    {
        var allSucceeded = true;

        foreach (var slot in slots)
        {
            if (!NativeInventoryHelper.TryGetEquippedItem(slot, out var itemId, out var stain0, out var stain1))
            {
                this.log.Warning($"[Flash] Could not read real equipped item for slot {slot} - leaving as-is.");
                onSlotResult?.Invoke($"{slot}: couldn't read real item, skipped");
                allSucceeded = false;
                continue;
            }

            try
            {
                var result = this.setItem.Invoke(target.ObjectIndex, slot, itemId, new List<byte> { stain0, stain1 }, lockKey);

                if (result != GlamourerApiEc.Success && result != GlamourerApiEc.NothingDone)
                {
                    this.log.Warning($"[Flash] Glamourer.SetItem({slot}) (restore) returned {result}");
                    onSlotResult?.Invoke($"{slot}: FAILED ({result})");
                    allSucceeded = false;
                }
                else
                {
                    onSlotResult?.Invoke($"{slot}: ok ({result}, item {itemId})");
                }
            }
            catch (Exception ex)
            {
                this.log.Error(ex, $"[Flash] Failed to call Glamourer.SetItem for slot {slot} (restore)");
                onSlotResult?.Invoke($"{slot}: EXCEPTION ({ex.GetType().Name}: {ex.Message})");
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }
}
