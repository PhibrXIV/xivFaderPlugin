using System;

namespace faderPlugin.Data;

[Serializable]
public class FadeOverride
{
    public bool UseCustomFadeTimes { get; set; }
    public float EnterTransitionSpeedOverride { get; set; }
    public float ExitTransitionSpeedOverride { get; set; }

    public FadeOverride()
    {
        UseCustomFadeTimes = false;
        EnterTransitionSpeedOverride = 4.0f;
        ExitTransitionSpeedOverride = 1.0f;
    }
}
