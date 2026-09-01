using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;

namespace Flash;

public class PluginUi
{
    private readonly Plugin plugin;

    public bool IsOpen;

    private string newEmoteSearch = string.Empty;

    public PluginUi(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        if (!this.IsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(560, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Flash Config", ref this.IsOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = this.plugin.Configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref enabled))
        {
            this.plugin.Configuration.PluginEnabled = enabled;
            this.plugin.Configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Debug Log"))
            this.plugin.DebugLogWindow.IsOpen = true;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens a log of every emote you've used with its exact ID and name - use it to confirm detection is working and see exactly which emote matched.");

        var glamourerReady = this.plugin.Glamourer.IsAvailable();
        ImGui.SameLine();
        ImGui.TextColored(
            glamourerReady ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
            glamourerReady ? "Glamourer: connected" : "Glamourer: not detected");

        ImGui.Separator();
        ImGui.TextUnformatted("Gear mode:");
        ImGui.SameLine();

        var stripMode = this.plugin.Configuration.StripMode;

        var isSmallclothes = stripMode == StripMode.Smallclothes;
        if (ImGui.RadioButton("Smallclothes", isSmallclothes))
        {
            this.plugin.Configuration.StripMode = StripMode.Smallclothes;
            this.plugin.Configuration.Save();
        }

        ImGui.SameLine();

        var isEmperors = stripMode == StripMode.EmperorsSet;
        if (ImGui.RadioButton("Emperor's Set", isEmperors))
        {
            this.plugin.Configuration.StripMode = StripMode.EmperorsSet;
            this.plugin.Configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Slots to change:");

        if (ImGui.BeginTable("SlotsTable", 5))
        {
            foreach (var slot in GlamourerIpc.StrippableSlots)
            {
                ImGui.TableNextColumn();

                var slotEnabled = this.plugin.Configuration.EnabledSlots.Contains(slot);
                if (ImGui.Checkbox(GlamourerIpc.SlotDisplayNames[slot], ref slotEnabled))
                {
                    if (slotEnabled)
                        this.plugin.Configuration.EnabledSlots.Add(slot);
                    else
                        this.plugin.Configuration.EnabledSlots.Remove(slot);

                    this.plugin.Configuration.Save();
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextWrapped("Add a mapping: search for an emote and click Add. When it's used, the checked " +
                           "slots above are replaced per the mode above. It reverts once the animation " +
                           "finishes, or sooner if Use Duration is checked below.");

        this.DrawAddRow();

        ImGui.Separator();
        this.DrawEntryTable();

        ImGui.End();
    }

    private void DrawAddRow()
    {
        ImGui.InputTextWithHint("##emoteSearch", "Emote name (e.g. Dance, Salute)", ref this.newEmoteSearch, 64);

        var sheet = Plugin.DataManager.GetExcelSheet<Emote>();
        Emote? matched = null;

        if (!string.IsNullOrWhiteSpace(this.newEmoteSearch) && sheet != null)
        {
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    continue;

                if (name.Contains(this.newEmoteSearch, StringComparison.OrdinalIgnoreCase))
                {
                    matched = row;
                    break;
                }
            }
        }

        if (matched != null)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), $"Matched: {matched.Value.Name.ExtractText()} (Id {matched.Value.RowId})");
        }
        else if (!string.IsNullOrWhiteSpace(this.newEmoteSearch))
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "No exact match yet - keep typing.");
        }

        ImGui.BeginDisabled(matched == null);
        if (ImGui.Button("Add mapping"))
        {
            this.plugin.Configuration.Entries.Add(new EmoteGearEntry
            {
                EmoteId = matched!.Value.RowId,
                EmoteName = matched.Value.Name.ExtractText(),
                TriggerDelaySeconds = 0f,
                LocalPlayerOnly = true,
                Enabled = true,
            });
            this.plugin.Configuration.Save();

            this.newEmoteSearch = string.Empty;
        }

        ImGui.EndDisabled();
    }

    private void DrawEntryTable()
    {
        var entries = this.plugin.Configuration.Entries;

        if (entries.Count == 0)
        {
            ImGui.TextDisabled("No mappings configured yet.");
            return;
        }

        if (!ImGui.BeginTable("EmoteGearTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28f);
        ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Delay (s)", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f);
        ImGui.TableSetupColumn("Duration (s)", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 32f);
        ImGui.TableHeadersRow();

        var toRemove = -1;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

            // Reorder via up/down buttons - simpler and more reliable than drag-and-drop.
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(i == 0);
            if (ImGui.SmallButton("^"))
            {
                (entries[i], entries[i - 1]) = (entries[i - 1], entries[i]);
                this.plugin.Configuration.Save();
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(i == entries.Count - 1);
            if (ImGui.SmallButton("v"))
            {
                (entries[i], entries[i + 1]) = (entries[i + 1], entries[i]);
                this.plugin.Configuration.Save();
            }

            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            var rowEnabled = entry.Enabled;
            if (ImGui.Checkbox("##enabled", ref rowEnabled))
            {
                entry.Enabled = rowEnabled;
                this.plugin.Configuration.Save();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{entry.EmoteName} ({entry.EmoteId})");

            ImGui.TableNextColumn();
            var triggerDelay = entry.TriggerDelaySeconds;
            ImGui.SetNextItemWidth(80);
            if (ImGui.DragFloat("##triggerDelay", ref triggerDelay, 0.1f, 0.0f, 60.0f, "%.1f"))
            {
                entry.TriggerDelaySeconds = triggerDelay;
                this.plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How long to wait after the emote starts before gear changes.");

            ImGui.TableNextColumn();
            var useDuration = entry.UseDuration;
            if (ImGui.Checkbox("##useDuration", ref useDuration))
            {
                entry.UseDuration = useDuration;
                this.plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("If off, gear reverts only when the animation actually finishes. If on, it also force-reverts after Duration, whichever comes first.");

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!entry.UseDuration);
            var duration = entry.DurationSeconds;
            ImGui.SetNextItemWidth(80);
            if (ImGui.DragFloat("##duration", ref duration, 0.1f, 0.0f, 300.0f, "%.1f"))
            {
                entry.DurationSeconds = duration;
                this.plugin.Configuration.Save();
            }

            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            if (ImGui.Button("X"))
                toRemove = i;

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (toRemove >= 0)
        {
            entries.RemoveAt(toRemove);
            this.plugin.Configuration.Save();
        }
    }
}
