using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Flash;

public class DebugLogUi
{
    private readonly Plugin plugin;

    public bool IsOpen;

    public DebugLogUi(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        if (!this.IsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(520, 400), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Flash Debug Log", ref this.IsOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("Every emote or action you use shows up here with its exact ID and name - " +
                           "use these in the manual ID override field in Flash Config when a name search " +
                           "grabs the wrong sheet row. This only covers your own local-player emotes/actions " +
                           "(the native-detected ones that actually carry a numeric ID); other players' " +
                           "emotes still go through Debug Mode's chat echo instead.");

        if (ImGui.Button("Clear"))
            this.plugin.DebugLog.Clear();

        ImGui.SameLine();
        ImGui.TextDisabled($"{this.plugin.DebugLog.Count} entries (newest first, capped at {Plugin.MaxDebugLogEntries})");

        ImGui.Separator();

        if (!ImGui.BeginTable("FlashDebugLogTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            ImGui.End();
            return;
        }

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Matched", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        for (var i = this.plugin.DebugLog.Count - 1; i >= 0; i--)
        {
            var entry = this.plugin.DebugLog[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Time.ToLocalTime().ToString("HH:mm:ss"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Source);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Id.ToString());
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText(entry.Id.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Name);

            ImGui.TableNextColumn();
            ImGui.TextColored(
                entry.Matched ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f),
                entry.Matched ? "Yes" : "No");

            ImGui.PopID();
        }

        ImGui.EndTable();
        ImGui.End();
    }
}
