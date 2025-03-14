using FaderPlugin.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaderPlugin.Utils;

public class DelayManager
{
    private readonly Dictionary<string, DateTime> _delayTimers = new();
    private readonly Dictionary<string, ConfigEntry> _lastNonDefaultEntry = new();

    public void RecordNonDefaultState(string addonName, ConfigEntry config, DateTime now)
    {
        _delayTimers[addonName] = now;
        _lastNonDefaultEntry[addonName] = config;
    }

    public ConfigEntry? GetDelayedConfig(string addonName, ConfigEntry defaultCandidate, DateTime now, double delayMs)
    {
        if (_delayTimers.TryGetValue(addonName, out var start))
        {
            if ((now - start).TotalMilliseconds < delayMs)
            {
                if (_lastNonDefaultEntry.TryGetValue(addonName, out var nonDefault))
                {
                    return nonDefault;
                }
            }
            else
            {
                // Delay expired; clear stored values.
                Clear(addonName);
            }
        }
        return defaultCandidate;
    }

    public void ClearAll()
    {
        _delayTimers.Clear();
        _lastNonDefaultEntry.Clear();
    }

    public void Clear(string addonName)
    {
        _delayTimers.Remove(addonName);
        _lastNonDefaultEntry.Remove(addonName);
    }
}
