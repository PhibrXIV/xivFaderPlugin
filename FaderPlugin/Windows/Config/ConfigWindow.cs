using System;
using System.Numerics;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FaderPlugin.Data;

namespace FaderPlugin.Windows.Config;

public partial class ConfigWindow : Window, IDisposable
{
    private readonly Plugin Plugin;
    private readonly Configuration Configuration;

    public ConfigWindow(Plugin plugin) : base("Configuration##Fader")
    {
        Plugin = plugin;
        Configuration = plugin.Config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(730, 670),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        using var tabBar = ImRaii.TabBar("##ConfigTabBar");
        if (!tabBar.Success)
            return;

        Settings();

        About();
    }
}
