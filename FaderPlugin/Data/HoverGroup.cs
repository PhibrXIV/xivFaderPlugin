using faderPlugin.Resources;
using System;
using System.Collections.Generic;

namespace FaderPlugin.Data;

[Serializable]
public class HoverGroup
{
    public string GroupName { get; set; }
    public List<Element> Elements { get; set; }

    public HoverGroup()
    {
        GroupName = Language.HoverGroupNewGroup;
        Elements = [];
    }
}

