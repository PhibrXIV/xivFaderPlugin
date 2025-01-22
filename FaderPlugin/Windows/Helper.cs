using Dalamud.Interface.Utility.Raii;
using ImGuiNET;

namespace FaderPlugin.Windows;

public static class Helper
{
    public static void WrappedText(string text)
    {
        using (ImRaii.TextWrapPos(0.0f))
        {
            ImGui.TextUnformatted(text);
        }
    }

    public static void Tooltip(string tooltip)
    {
        using (ImRaii.Tooltip())
        using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 35.0f))
        {
            ImGui.TextUnformatted(tooltip);
        }
    }
}
