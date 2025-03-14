using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace FaderPlugin.Data;

public class ConfigEntry
{
    public State state { get; set; }
    public Setting setting { get; set; }
    public float Opacity { get; set; } = 1.0f;

    public ConfigEntry(State state, Setting setting)
    {
        this.state = state;
        this.setting = setting;
    }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public event Action? OnSave;

    public int Version { get; set; } = 6;
    public Dictionary<Element, List<ConfigEntry>> elementsConfig { get; set; }
    public bool DefaultDelayEnabled { get; set; } = true;
    public int DefaultDelay { get; set; } = 2000;
    public int ChatActivityTimeout { get; set; } = 5 * 1000;
    public int OverrideKey { get; set; } = 0x12;
    public bool FocusOnHotbarsUnlock { get; set; } = false;
    public bool EmoteActivity { get; set; } = false;
    public bool ImportantActivity { get; set; } = false;
    public float DefaultAlpha { get; set; } = 1.0f;
    public float EnterTransitionSpeed { get; set; } = 3.0f; // change in alpha per frame when fading in
    public float ExitTransitionSpeed { get; set; } = 0.5f; // change in alpha per frame when fading out


    public void Initialize()
    {
        // Initialise the config.
        elementsConfig ??= new Dictionary<Element, List<ConfigEntry>>();
        foreach (var element in Enum.GetValues<Element>())
        {
            if (!elementsConfig.ContainsKey(element))
                elementsConfig[element] = new List<ConfigEntry> { new ConfigEntry(State.Default, Setting.Show) };
        }
        Save();
    }

    public List<ConfigEntry> GetElementConfig(Element elementId)
    {
        if (!elementsConfig.ContainsKey(elementId))
            elementsConfig[elementId] = new List<ConfigEntry>();

        return elementsConfig[elementId];
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
        OnSave?.Invoke();
    }
}