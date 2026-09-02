using FFXIVClientStructs.FFXIV.Client.Game;
using Glamourer.Api.Enums;

namespace Flash;

/// <summary>
/// Reads a character's real (actually equipped, not Glamourer-overridden) gear directly
/// from FFXIVClientStructs' InventoryManager. Used to restore armor/accessory slots
/// after a strip WITHOUT ever calling any Glamourer API that touches the whole equipped
/// set (like ApplyState with the Equipment flag) - that was reapplying weapon slots too
/// as a side effect, causing an unwanted weapon redraw/VFX reset on every revert.
///
/// CONFIDENCE NOTE: InventoryManager.GetInventoryContainer(InventoryType.EquippedItems),
/// the fixed EquippedItems slot ordering (MainHand, OffHand, Head, Body, Hands, Waist,
/// Legs, Feet, Ears, Neck, Wrists, RFinger, LFinger, SoulCrystal), and InventoryItem's
/// ItemId/Stain fields are all confirmed - ItemId and the container plumbing compiled
/// clean on the first try, and Stain was independently confirmed against Dalamud's own
/// API docs after a build error showed the original guessed name (Stain0Id) was wrong.
/// Stain2 (the second dye channel) follows Ottermandias's usual naming convention but
/// wasn't independently re-verified the same way - if this doesn't compile, "Go to
/// Definition" on InventoryItem in the IDE will show the real second field name.
/// </summary>
public static unsafe class NativeInventoryHelper
{
    /// <summary>
    /// Trade-off, stated plainly: this reads the character's REAL equipped item for the
    /// slot, not whatever Glamourer might currently be showing there. If you'd used
    /// Glamourer to visually override a specific armor piece (as opposed to a
    /// body/gender customization change, which is handled separately and does survive
    /// a strip/revert cycle), that specific armor override will not survive a Flash
    /// strip/revert - the real item comes back instead. This was accepted as the cost
    /// of guaranteeing weapons are never touched.
    /// </summary>
    public static bool TryGetEquippedItem(ApiEquipSlot slot, out uint itemId, out byte stain0, out byte stain1)
    {
        itemId = 0;
        stain0 = 0;
        stain1 = 0;

        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null)
            return false;

        var index = SlotToContainerIndex(slot);
        if (index < 0)
            return false;

        var item = container->GetInventorySlot(index);
        if (item == null)
            return false;

        itemId = item->ItemId;
        // "Stain" confirmed directly from Dalamud's own API docs. "Stain2" (the second
        // dye channel) follows Ottermandias's usual naming convention but wasn't
        // independently verified the same way - if this doesn't compile, "Go to
        // Definition" on InventoryItem will show the real second field name.
        stain0 = item->Stain;
        stain1 = item->Stain2;
        return true;
    }

    private static int SlotToContainerIndex(ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Head => 2,
        ApiEquipSlot.Body => 3,
        ApiEquipSlot.Hands => 4,
        ApiEquipSlot.Legs => 6,
        ApiEquipSlot.Feet => 7,
        ApiEquipSlot.Ears => 8,
        ApiEquipSlot.Neck => 9,
        ApiEquipSlot.Wrists => 10,
        ApiEquipSlot.RFinger => 11,
        ApiEquipSlot.LFinger => 12,
        _ => -1, // Deliberately excludes MainHand/OffHand - Flash never reads or writes weapon slots.
    };
}
