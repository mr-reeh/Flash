using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Emote Config", ref this.IsOpen))
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
        var debug = this.plugin.Configuration.DebugMode;
        if (ImGui.Checkbox("Debug mode", ref debug))
        {
            this.plugin.Configuration.DebugMode = debug;
            this.plugin.Configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Echoes every emote detected to chat, matched or not - use this to confirm detection is firing.");

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
        ImGui.TextWrapped("Add a mapping: search for an emote and click Add. When it's used, gear in " +
                           "head/body/hands/legs/feet/ears/neck/wrists/rings is replaced per the mode above, " +
                           "and stays that way until the animation changes to something not configured.");

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

        if (!ImGui.BeginTable("EmoteGearTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("On");
        ImGui.TableSetupColumn("Emote");
        ImGui.TableSetupColumn("Delay (s)");
        ImGui.TableSetupColumn("");
        ImGui.TableHeadersRow();

        var toRemove = -1;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

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
            if (ImGui.Button("Remove"))
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
