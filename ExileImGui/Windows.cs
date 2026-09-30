using System;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui;

/// <summary>
/// Window, tab and pane frames the controls sit in.
/// </summary>
public static class Windows
{
    /// <summary>
    /// A window with a first-run size. Call it only when you mean to show the window.
    /// </summary>
    /// <param name="open">Wired to the title-bar close X.</param>
    /// <param name="firstSize">Applied on first use only, so a user resize sticks afterwards.</param>
    /// <returns>False when the window is collapsed. Skip the body then, but ALWAYS call
    /// <see cref="End"/> either way - this is the classic ImGui gotcha.</returns>
    public static bool Begin(string title, string id, ref bool open, ImGuiWindowFlags flags = 0, Vector2? firstSize = null)
    {
        if (firstSize.HasValue) ImGui.SetNextWindowSize(firstSize.Value, ImGuiCond.FirstUseEver);
        return ImGui.Begin(title + "##" + id, ref open, flags);
    }

    /// <summary>Closes a <see cref="Begin"/>, whatever it returned.</summary>
    public static void End() => ImGui.End();

    /// <summary>
    /// Tab bar over (label, body) pairs.
    /// </summary>
    /// <param name="tabs">Each body draws one tab and returns its own changed flag.</param>
    /// <returns>The OR of whichever bodies actually drew, so the whole page rolls up to one dirty flag.</returns>
    public static bool TabBar(string id, params (string label, Func<bool> body)[] tabs)
    {
        bool changed = false;
        if (ImGui.BeginTabBar("##" + id))
        {
            foreach (var (label, body) in tabs)
            {
                if (ImGui.BeginTabItem(label))
                {
                    changed |= body();
                    ImGui.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }
        return changed;
    }

    /// <summary>
    /// Fixed-width list on the left, detail pane on the right. You draw both sides.
    /// </summary>
    /// <param name="leftWidth">Pixel width of the left child; the right one takes the rest.</param>
    public static void MasterDetail(string id, float leftWidth, Action left, Action right)
    {
        ImGui.PushID(id);
        ImGui.BeginChild("##l", new Vector2(leftWidth, 0), ImGuiChildFlags.Border);
        left();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##r", new Vector2(0, 0), ImGuiChildFlags.Border);
        right();
        ImGui.EndChild();
        ImGui.PopID();
    }

    /// <summary>
    /// Two panels side by side, stacked once the pane narrows past <paramref name="minWidth"/> - two
    /// columns of 130px read worse than one of 260. The table's inner border is the divider.
    /// <para>
    /// The left column measures its own content (WidthFixed with no width), so long labels don't
    /// clip and the right one takes the rest. Careful what you put in the left one: anything that
    /// sizes itself to "the available width" is circular in a column that is measuring its content,
    /// and comes out a few pixels wide. Give those an explicit width.
    /// </para>
    /// </summary>
    /// <returns>The OR of both bodies' changed flags.</returns>
    public static bool TwoColumn(string id, float minWidth, Func<bool> left, Func<bool> right)
    {
        if (ImGui.GetContentRegionAvail().X < minWidth)
        {
            bool stacked = left();
            ImGui.Separator();
            return right() || stacked;
        }

        if (!ImGui.BeginTable("##" + id, 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
            return false;
        ImGui.TableSetupColumn("l", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("r", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        bool d = left();
        ImGui.TableNextColumn();
        d = right() || d;
        ImGui.EndTable();
        return d;
    }

    /// <summary>
    /// The left half of a master-detail: a [title .... count] row, a filter box, a scrolling body,
    /// and a hint pinned to the bottom. The reserved strip is measured off the WRAPPED hint, so it
    /// stays exact as the pane narrows - a hardcoded "two lines" clips the moment it wraps to three.
    /// </summary>
    /// <param name="filter">Caller-owned filter text.</param>
    /// <param name="hint">Bottom-pinned help line. Empty reserves no strip.</param>
    /// <param name="body">Draws the rows, usually ListEditor.ReorderableList, and owns its own changed flag.</param>
    public static void FilterPane(string id, string title, int count, ref string filter, string hint, Action body)
    {
        ImGui.PushID(id);
        ImGui.TextDisabled(Text.Ascii(title));
        Controls.BeginTrailing(id + "_count");   // keyed on the pane: the width cache is app-wide
        ImGui.TextDisabled(count.ToString());
        Controls.EndTrailing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter", ref filter, 64);

        float hintH = string.IsNullOrEmpty(hint)
            ? 0f
            : ImGui.CalcTextSize(hint, false, ImGui.GetContentRegionAvail().X).Y + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("##rows", new Vector2(0, -hintH));
        body();
        ImGui.EndChild();

        if (!string.IsNullOrEmpty(hint))
        {
            // wrap at the right edge of this pane. without it the hint just runs off the side.
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(Text.Ascii(hint));
            ImGui.PopTextWrapPos();
        }
        ImGui.PopID();
    }

    /// <summary>
    /// Yes/no modal. Open it with <c>ImGui.OpenPopup(id)</c> first, then call this every frame.
    /// </summary>
    /// <param name="confirmed">True when Confirm was the button pressed.</param>
    /// <returns>True the frame either button is pressed.</returns>
    public static bool ConfirmModal(string id, string message, out bool confirmed)
    {
        confirmed = false;
        bool acted = false;
        // imgui.net 1.90 has no BeginPopupModal(id, flags) overload, so we pass a throwaway open flag.
        // effect: the title-bar close X is inert, confirm/cancel drive the modal. fine for a confirm dialog.
        bool open = true;
        if (ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            using (new EColor.StyleColorScope((ImGuiCol.Text, 0xFF0000FFu))) // red = 0xAABBGGRR
                ImGui.TextUnformatted(message);
            if (ImGui.Button("Confirm")) { confirmed = true; acted = true; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { acted = true; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        return acted;
    }
}
