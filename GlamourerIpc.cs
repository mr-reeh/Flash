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
/// GEAR RESTORE DESIGN: RestoreGearFromState is the primary/normal restore path - it
/// decodes the state string captured via CaptureState (base64 -> gzip, header confirmed
/// via a live dump: a short prefix byte before the standard 1F 8B gzip magic) into JSON,
/// then reads each slot's real ItemId/Stain/Stain2 directly from
/// Equipment.&lt;SlotName&gt; and writes it back via the same SetItem calls StripAllGear
/// uses. Since this never calls ApplyState/RevertState, it never touches Customize data
/// either - meaning gender/body/customization changes are naturally preserved because
/// Flash never disturbs them in the first place, not because anything explicitly
/// restores them. This also avoids the weapon redraw entirely: testing confirmed
/// ApplyState triggers a full character redraw UNCONDITIONALLY regardless of flags
/// (even Customization-only redrew weapons), independently corroborated by Glamourer's
/// own changelog ("the redraw done by Glamourer's ApplyState") - SetItem alone does not.
/// RestoreState (ApplyState-based) and Revert (RevertState-based, discards all
/// overrides) remain as two-tier fallbacks for the rare case where JSON decode/parse
/// fails (e.g. a future Glamourer update changes the schema).
/// </summary>
public class GlamourerIpc
{
    private readonly IPluginLog log;

    private readonly ApiVersion apiVersion;
    private readonly SetItem setItem;
    private readonly RevertState revertState;
    private readonly GetStateBase64 getStateBase64;
    private readonly ApplyState applyState;

    private Dictionary<ApiEquipSlot, uint>? emperorsSetItemIds;

    /// <summary>
    /// The equipment slots this plugin strips/swaps/restores via SetItem. Deliberately
    /// excludes MainHand/OffHand (weapons) - Flash never touches weapon slots, on either
    /// the strip or (normal-path) restore side. Which of these are actually touched on
    /// a given strip is controlled by Configuration.EnabledSlots, not this list - this
    /// is the master set used to build the checkbox UI and as the default when
    /// EnabledSlots isn't yet configured.
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
        this.revertState = new RevertState(pi);
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
    /// Decodes a state string captured via CaptureState into its underlying JSON.
    /// Confirmed via a live dump: the base64-decoded bytes are a short prefix (byte 0
    /// was 0x06 in testing - likely a format/version marker) followed immediately by a
    /// standard gzip stream (magic bytes 1F 8B). This searches for that gzip header
    /// rather than assuming a fixed prefix length, in case the prefix size varies.
    /// </summary>
    public static string? DecodeState(string base64State, out string? error)
    {
        error = null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64State);
        }
        catch (Exception ex)
        {
            error = $"Base64 decode failed: {ex.Message}";
            return null;
        }

        var gzipStart = -1;
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0x1F && bytes[i + 1] == 0x8B)
            {
                gzipStart = i;
                break;
            }
        }

        if (gzipStart < 0)
        {
            error = $"No gzip header (1F 8B) found anywhere in {bytes.Length} bytes. " +
                    $"First 16 bytes (hex): {Convert.ToHexString(bytes, 0, Math.Min(16, bytes.Length))}";
            return null;
        }

        try
        {
            using var input = new System.IO.MemoryStream(bytes, gzipStart, bytes.Length - gzipStart);
            using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new System.IO.MemoryStream();
            gzip.CopyTo(output);
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception ex)
        {
            error = $"Found gzip header at offset {gzipStart} but decompression failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>TEMPORARY DIAGNOSTIC wrapper around DecodeState for /flash dumpstate -
    /// returns the decoded JSON or an error string, never null, for easy logging.</summary>
    public static string DecodeStateForDiagnostics(string base64State)
    {
        var decoded = DecodeState(base64State, out var error);
        return decoded ?? $"[Flash] {error}";
    }

    /// <summary>
    /// Restores the configured slots by reading each one's ItemId/Stain/Stain2 directly
    /// out of a state string previously captured via CaptureState, then writing it back
    /// via SetItem - never calling ApplyState/RevertState, so this never touches
    /// Customize data and never triggers Glamourer's unconditional redraw. See the class
    /// doc comment for the full reasoning. Returns false if the state couldn't be
    /// decoded/parsed at all (callers should fall back to RestoreState, then Revert);
    /// still attempts every slot even if one is missing from the parsed data, so a
    /// partial schema mismatch doesn't block the rest.
    /// </summary>
    public bool RestoreGearFromState(ICharacter target, string state, IEnumerable<ApiEquipSlot> slots, uint lockKey = 0, Action<string>? onSlotResult = null)
    {
        var json = DecodeState(state, out var decodeError);
        if (json == null)
        {
            this.log.Warning($"[Flash] Could not decode captured state for restore: {decodeError}");
            return false;
        }

        GlamourerStateRoot? root;
        try
        {
            root = System.Text.Json.JsonSerializer.Deserialize<GlamourerStateRoot>(json);
        }
        catch (Exception ex)
        {
            this.log.Warning($"[Flash] Could not parse captured state JSON: {ex.Message}");
            return false;
        }

        if (root?.Equipment == null)
        {
            this.log.Warning("[Flash] Captured state had no Equipment section.");
            return false;
        }

        var allSucceeded = true;

        foreach (var slot in slots)
        {
            var slotData = GetSlotData(root.Equipment, slot);
            if (slotData == null)
            {
                this.log.Warning($"[Flash] Captured state had no data for slot {slot} - leaving as-is.");
                onSlotResult?.Invoke($"{slot}: no data in captured state, skipped");
                allSucceeded = false;
                continue;
            }

            try
            {
                var result = this.setItem.Invoke(target.ObjectIndex, slot, slotData.ItemId, new List<byte> { slotData.Stain, slotData.Stain2 }, lockKey);

                if (result != GlamourerApiEc.Success && result != GlamourerApiEc.NothingDone)
                {
                    this.log.Warning($"[Flash] Glamourer.SetItem({slot}) (restore) returned {result}");
                    onSlotResult?.Invoke($"{slot}: FAILED ({result})");
                    allSucceeded = false;
                }
                else
                {
                    onSlotResult?.Invoke($"{slot}: ok ({result}, item {slotData.ItemId})");
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

    private static GlamourerStateEquipmentSlot? GetSlotData(GlamourerStateEquipment equipment, ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Head => equipment.Head,
        ApiEquipSlot.Body => equipment.Body,
        ApiEquipSlot.Hands => equipment.Hands,
        ApiEquipSlot.Legs => equipment.Legs,
        ApiEquipSlot.Feet => equipment.Feet,
        ApiEquipSlot.Ears => equipment.Ears,
        ApiEquipSlot.Neck => equipment.Neck,
        ApiEquipSlot.Wrists => equipment.Wrists,
        ApiEquipSlot.RFinger => equipment.RFinger,
        ApiEquipSlot.LFinger => equipment.LFinger,
        _ => null,
    };

    // Minimal JSON model matching only the fields Flash actually reads, confirmed
    // directly from a live decoded state dump (see /flash dumpstate). Property names
    // match the real JSON keys exactly (PascalCase), so no [JsonPropertyName] needed.
    private class GlamourerStateRoot
    {
        public GlamourerStateEquipment? Equipment { get; set; }
    }

    private class GlamourerStateEquipment
    {
        public GlamourerStateEquipmentSlot? Head { get; set; }
        public GlamourerStateEquipmentSlot? Body { get; set; }
        public GlamourerStateEquipmentSlot? Hands { get; set; }
        public GlamourerStateEquipmentSlot? Legs { get; set; }
        public GlamourerStateEquipmentSlot? Feet { get; set; }
        public GlamourerStateEquipmentSlot? Ears { get; set; }
        public GlamourerStateEquipmentSlot? Neck { get; set; }
        public GlamourerStateEquipmentSlot? Wrists { get; set; }
        public GlamourerStateEquipmentSlot? RFinger { get; set; }
        public GlamourerStateEquipmentSlot? LFinger { get; set; }
    }

    private class GlamourerStateEquipmentSlot
    {
        public uint ItemId { get; set; }
        public byte Stain { get; set; }
        public byte Stain2 { get; set; }
    }

    /// <summary>
    /// Captures the character's full current Glamourer state (any active design/
    /// customization/gender override, not just gear) as an opaque string. Primarily
    /// used by RestoreGearFromState (parses this into JSON to read per-slot data
    /// directly); also kept as-is for the RestoreState fallback tier. Returns null on
    /// failure.
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

    /// <summary>Fallback tier 2: restores a state string via Glamourer's own ApplyState
    /// (Equipment | Customization) - only used when RestoreGearFromState couldn't decode
    /// or parse the captured state. Triggers Glamourer's unconditional redraw (weapons
    /// included) as a side effect - see the class doc comment. Returns false on failure,
    /// callers should fall back further to Revert.</summary>
    public bool RestoreState(ICharacter target, string state, uint lockKey = 0)
    {
        try
        {
            var result = this.applyState.Invoke(
                state,
                target.ObjectIndex,
                lockKey,
                ApplyFlag.Once | ApplyFlag.Equipment | ApplyFlag.Customization);

            if (result != GlamourerApiEc.Success && result != GlamourerApiEc.NothingDone)
            {
                this.log.Warning($"[Flash] Glamourer.ApplyState returned {result}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Flash] Failed to call Glamourer.ApplyState");
            return false;
        }
    }

    /// <summary>Fallback tier 3 (last resort): reverts the given actor back to their
    /// real equipped gear, discarding any active Glamourer override entirely. Only used
    /// when no state snapshot exists or both RestoreGearFromState and RestoreState
    /// failed.</summary>
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
