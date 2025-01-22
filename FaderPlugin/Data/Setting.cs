using System;
using faderPlugin.Resources;

namespace FaderPlugin.Data;

public enum Setting
{
    Show,
    Hide,
    Unknown
}

public static class SettingExtensions
{
    public static string GetName(this Setting setting)
    {
        return setting switch
        {
            Setting.Show => Language.SettingsShow,
            Setting.Hide => Language.SettingsHide,
            Setting.Unknown => Language.SettingsUnknown,
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null)
        };
    }
}