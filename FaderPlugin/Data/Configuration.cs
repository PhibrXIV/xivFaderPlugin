﻿using System;
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
    public float EnterTransitionSpeed { get; set; } = 4.0f; // alpha change per frame when fading in
    public float ExitTransitionSpeed { get; set; } = 1.0f; // alpha change per frame when fading out (1.0f = 1 second for full transition)


    public void Initialize()
    {
        // Initialise the config.
        elementsConfig ??= new Dictionary<Element, List<ConfigEntry>>();
        foreach (var element in Enum.GetValues<Element>())
        {
            if (!elementsConfig.ContainsKey(element))
                elementsConfig[element] = new List<ConfigEntry> { new ConfigEntry(State.Default, Setting.Show) };
        }
        FixLegacyConfig();
        Save();
    }

    public void FixLegacyConfig()
    {
        foreach (var entries in elementsConfig.Values)
        {
            foreach (var entry in entries)
            {
                // If the entry is set to Hide and its opacity is above 0.05 (which old configurations will be),
                // then update it to 0 to keep the configuration working as before.
                if (entry.setting == Setting.Hide && entry.Opacity > 0.05f)
                {
                    entry.Opacity = 0;
                }
            }
        }
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