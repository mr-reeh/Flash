using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Flash;

public class PluginUi
{
    private readonly Plugin plugin;

    public bool IsOpen;

    private TriggerType newTriggerType = TriggerType.Emote;
    private string newEmoteSearch = string.Empty;
    private int newEmoteIdOverride;
    private string newActionSearch = string.Empty;
    private int newActionIdOverride;

    public PluginUi(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        if (!this.IsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(560, 600), ImGuiCond.FirstUseEver);
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
            ImGui.SetTooltip("Opens a log of every emote/action you've used with its exact ID and name - use it to confirm detection is working and see exactly which trigger matched.");

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
        ImGui.TextWrapped("Add a mapping: pick Emote or Action, search by name, and click Add. When " +
                           "triggered, the checked slots above are replaced per the mode above. Emote " +
                           "mappings revert once the animation finishes (or sooner with Duration); Action " +
                           "mappings always use Duration.");

        this.DrawAddRow();

        ImGui.Separator();
        this.DrawEntryTable();

        ImGui.End();
    }

    private void DrawAddRow()
    {
        ImGui.TextUnformatted("New trigger:");
        ImGui.SameLine();

        if (ImGui.RadioButton("Emote##triggerType", this.newTriggerType == TriggerType.Emote))
            this.newTriggerType = TriggerType.Emote;

        ImGui.SameLine();

        if (ImGui.RadioButton("Action##triggerType", this.newTriggerType == TriggerType.Action))
            this.newTriggerType = TriggerType.Action;

        if (this.newTriggerType == TriggerType.Emote)
            this.DrawAddEmoteRow();
        else
            this.DrawAddActionRow();
    }

    private void DrawAddEmoteRow()
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

        ImGui.TextWrapped("Name search matches the first emote whose name contains your text, so a short " +
                           "search (e.g. \"Hum\") can grab the wrong one (\"Thumbs Up\" before \"Hum\"). If " +
                           "that happens, check the Flash Debug Log for the exact ID and enter it directly " +
                           "below instead.");

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Emote ID override (optional)", ref this.newEmoteIdOverride);

        if (this.newEmoteIdOverride > 0 && sheet != null)
        {
            var overrideRow = sheet.GetRowOrDefault((uint)this.newEmoteIdOverride);
            var overrideName = overrideRow?.Name.ExtractText();
            var displayName = string.IsNullOrEmpty(overrideName)
                ? "(unknown - not in Emote sheet, will still be saved)"
                : overrideName;
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), $"Will use: {displayName} (Id {this.newEmoteIdOverride})");
        }

        var hasOverride = this.newEmoteIdOverride > 0;
        ImGui.BeginDisabled(matched == null && !hasOverride);
        if (ImGui.Button("Add mapping"))
        {
            uint emoteId;
            string emoteName;

            if (hasOverride)
            {
                emoteId = (uint)this.newEmoteIdOverride;
                var overrideRow = sheet?.GetRowOrDefault(emoteId);
                var overrideName = overrideRow?.Name.ExtractText();
                emoteName = string.IsNullOrEmpty(overrideName) ? $"Emote {emoteId}" : overrideName;
            }
            else
            {
                emoteId = matched!.Value.RowId;
                emoteName = matched.Value.Name.ExtractText();
            }

            this.plugin.Configuration.Entries.Add(new EmoteGearEntry
            {
                TriggerType = TriggerType.Emote,
                EmoteId = emoteId,
                EmoteName = emoteName,
                TriggerDelaySeconds = 0f,
                LocalPlayerOnly = true,
                Enabled = true,
            });
            this.plugin.Configuration.Save();

            this.newEmoteSearch = string.Empty;
            this.newEmoteIdOverride = 0;
        }

        ImGui.EndDisabled();
    }

    private void DrawAddActionRow()
    {
        ImGui.InputTextWithHint("##actionSearch", "Action name (e.g. Provoke)", ref this.newActionSearch, 64);

        var sheet = Plugin.DataManager.GetExcelSheet<LuminaAction>();
        LuminaAction? matched = null;

        if (!string.IsNullOrWhiteSpace(this.newActionSearch) && sheet != null)
        {
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    continue;

                if (name.Contains(this.newActionSearch, StringComparison.OrdinalIgnoreCase))
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
        else if (!string.IsNullOrWhiteSpace(this.newActionSearch))
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "No exact match yet - keep typing.");
        }

        ImGui.TextWrapped("The Action sheet has duplicate/legacy rows sharing the same name, so a text " +
                           "search can grab the wrong one. If a mapping doesn't fire, use the action, check " +
                           "the Flash Debug Log for the real ID, and enter it directly below instead.");

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Action ID override (optional)", ref this.newActionIdOverride);

        if (this.newActionIdOverride > 0 && sheet != null)
        {
            var overrideRow = sheet.GetRowOrDefault((uint)this.newActionIdOverride);
            var overrideName = overrideRow?.Name.ExtractText();
            var displayName = string.IsNullOrEmpty(overrideName)
                ? "(unknown - not in Action sheet, will still be saved)"
                : overrideName;
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), $"Will use: {displayName} (Id {this.newActionIdOverride})");
        }

        ImGui.TextWrapped("Action mappings always use Duration (no animation to detect the end of) - " +
                           "defaults to a brief 2s flash; adjust it in the table below after adding.");

        var hasOverride = this.newActionIdOverride > 0;
        ImGui.BeginDisabled(matched == null && !hasOverride);
        if (ImGui.Button("Add mapping"))
        {
            uint actionId;
            string actionName;

            if (hasOverride)
            {
                actionId = (uint)this.newActionIdOverride;
                var overrideRow = sheet?.GetRowOrDefault(actionId);
                var overrideName = overrideRow?.Name.ExtractText();
                actionName = string.IsNullOrEmpty(overrideName) ? $"Action {actionId}" : overrideName;
            }
            else
            {
                actionId = matched!.Value.RowId;
                actionName = matched.Value.Name.ExtractText();
            }

            this.plugin.Configuration.Entries.Add(new EmoteGearEntry
            {
                TriggerType = TriggerType.Action,
                ActionId = actionId,
                ActionName = actionName,
                TriggerDelaySeconds = 0f,
                UseDuration = true,
                DurationSeconds = 2.0f,
                LocalPlayerOnly = true,
                Enabled = true,
            });
            this.plugin.Configuration.Save();

            this.newActionSearch = string.Empty;
            this.newActionIdOverride = 0;
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
            var triggerLabel = entry.TriggerType == TriggerType.Emote
                ? $"[Emote] {entry.EmoteName} ({entry.EmoteId})"
                : $"[Action] {entry.ActionName} ({entry.ActionId})";
            ImGui.TextUnformatted(triggerLabel);

            ImGui.TableNextColumn();
            var triggerDelay = entry.TriggerDelaySeconds;
            ImGui.SetNextItemWidth(80);
            if (ImGui.DragFloat("##triggerDelay", ref triggerDelay, 0.1f, 0.0f, 60.0f, "%.1f"))
            {
                entry.TriggerDelaySeconds = triggerDelay;
                this.plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How long to wait after the trigger fires before gear changes.");

            ImGui.TableNextColumn();
            var useDuration = entry.UseDuration;
            ImGui.BeginDisabled(entry.TriggerType == TriggerType.Action);
            if (ImGui.Checkbox("##useDuration", ref useDuration))
            {
                entry.UseDuration = useDuration;
                this.plugin.Configuration.Save();
            }

            if (entry.TriggerType == TriggerType.Action && ImGui.IsItemHovered())
                ImGui.SetTooltip("Action triggers always use Duration - there's no animation to detect the end of.");
            else if (ImGui.IsItemHovered())
                ImGui.SetTooltip("If off, gear reverts only when the animation actually finishes. If on, it also force-reverts after Duration, whichever comes first.");

            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!entry.UseDuration && entry.TriggerType != TriggerType.Action);
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
