using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Timers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FaderPlugin.Data;
using faderPlugin.Resources;
using FaderPlugin.Windows.Config;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace FaderPlugin;

public class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] public static IKeyState KeyState { get; set; } = null!;
    [PluginService] public static IFramework Framework { get; set; } = null!;
    [PluginService] public static IClientState ClientState { get; set; } = null!;
    [PluginService] public static ICondition Condition { get; set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; set; } = null!;
    [PluginService] public static IGameGui GameGui { get; set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; set; } = null!;
    [PluginService] public static IDataManager Data { get; private set; } = null!;

    public readonly Configuration Config;

    private readonly WindowSystem WindowSystem = new("Fader");
    private readonly ConfigWindow ConfigWindow;

    private readonly Dictionary<State, bool> StateMap = new();
    private bool StateChanged;

    // Idle State
    private readonly Timer IdleTimer = new();
    private bool HasIdled;

    // Chat State
    private readonly Timer ChatActivityTimer = new();
    private bool HasChatActivity;

    // Commands
    private const string CommandName = "/pfader";
    private bool Enabled = true;

    // Opacity State
    private readonly Dictionary<string, float> CurrentAlphas = new();


    private readonly ExcelSheet<TerritoryType> TerritorySheet;

    public Plugin()
    {
        LoadConfig(out Config);
        // Config.OnSave += UpdateAddonVisibility;

        LanguageChanged(PluginInterface.UiLanguage);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        TerritorySheet = Data.GetExcelSheet<TerritoryType>();

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(FaderCommandHandler)
        {
            HelpMessage = "Opens settings\n't' toggles whether it's enabled.\n'on' enables the plugin\n'off' disables the plugin."
        });

        foreach(State state in Enum.GetValues(typeof(State)))
            StateMap[state] = state == State.Default;

        // We don't want a looping timer, only once
        IdleTimer.AutoReset = false;
        IdleTimer.Elapsed += (_, _) => HasIdled = true;
        IdleTimer.Start();

        ChatActivityTimer.Elapsed += (_, _) => HasChatActivity = false;

        ChatGui.ChatMessage += OnChatMessage;
        PluginInterface.LanguageChanged += LanguageChanged;

        // Recover from previous misconfiguration
        if (Config.DefaultDelay == 0)
            Config.DefaultDelay = 2000;
    }

    public void Dispose()
    {
        PluginInterface.LanguageChanged -= LanguageChanged;
        Config.OnSave -= UpdateAddonVisibility;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        ChatGui.ChatMessage -= OnChatMessage;
        UpdateAddonVisibility(true);

        IdleTimer.Dispose();
        ChatActivityTimer.Dispose();

        ConfigWindow.Dispose();
        WindowSystem.RemoveWindow(ConfigWindow);
    }

    private void LanguageChanged(string langCode)
    {
        Language.Culture = new CultureInfo(langCode);
    }

    private void LoadConfig(out Configuration configuration)
    {
        var existingConfig = PluginInterface.GetPluginConfig();

        if(existingConfig is { Version: 6 })
            configuration = (Configuration) existingConfig;
        else
            configuration = new Configuration();

        configuration.Initialize();
    }

    private void DrawUi()
    {
        WindowSystem.Draw();
    }

    private void DrawConfigUi()
    {
        ConfigWindow.Toggle();
    }

    private void FaderCommandHandler(string s, string arguments)
    {
        switch (arguments.Trim())
        {
            case "t" or "toggle":
                Enabled = !Enabled;
                ChatGui.Print(Enabled ? Language.ChatPluginEnabled : Language.ChatPluginDisabled);
                break;
            case "on":
                Enabled = true;
                ChatGui.Print(Language.ChatPluginEnabled);
                break;
            case "off":
                Enabled = false;
                ChatGui.Print(Language.ChatPluginDisabled);
                break;
            case "":
                ConfigWindow.Toggle();
                break;
        }
    }

    private void OnChatMessage(XivChatType type, int _, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        // Don't trigger chat for non-standard chat channels.
        if (!Constants.ActiveChatTypes.Contains(type)
            && (!Config.ImportantActivity || !Constants.ImportantChatTypes.Contains(type))
            && (!Config.EmoteActivity || !Constants.EmoteChatTypes.Contains(type)))
            return;

        HasChatActivity = true;

        ChatActivityTimer.Stop();
        ChatActivityTimer.Interval = Config.ChatActivityTimeout;
        ChatActivityTimer.Start();
    }

    private unsafe void OnFrameworkUpdate(IFramework framework) {
        if(!IsSafeToWork())
            return;

        StateChanged = false;
        bool hoverDetected = false;
        // User Focus
        UpdateStateMap(State.UserFocus, KeyState[Config.OverrideKey] || (Config.FocusOnHotbarsUnlock && !Addon.AreHotbarsLocked()));

        // Key Focus
        UpdateStateMap(State.AltKeyFocus, KeyState[(int) Constants.OverrideKeys.Alt]);
        UpdateStateMap(State.CtrlKeyFocus, KeyState[(int) Constants.OverrideKeys.Ctrl]);
        UpdateStateMap(State.ShiftKeyFocus, KeyState[(int) Constants.OverrideKeys.Shift]);

        // Chat Focus
        UpdateStateMap(State.ChatFocus, Addon.IsChatFocused());

        // Chat Activity
        UpdateStateMap(State.ChatActivity, HasChatActivity);

        // Combat
        UpdateStateMap(State.IsMoving, Addon.IsMoving());

        // Combat
        UpdateStateMap(State.Combat, Condition[ConditionFlag.InCombat]);

        // Weapon Unsheathed
        UpdateStateMap(State.WeaponUnsheathed, Addon.IsWeaponUnsheathed());

        // In Sanctuary (e.g Cities, Aetheryte Villages)
        UpdateStateMap(State.InSanctuary, Addon.InSanctuary());

        // Island Sanctuary
        var inIslandSanctuary = TerritorySheet.HasRow(ClientState.TerritoryType) && TerritorySheet.GetRow(ClientState.TerritoryType).TerritoryIntendedUse.RowId == 49;
        UpdateStateMap(State.IslandSanctuary, inIslandSanctuary);

        // In Fate Area
        UpdateStateMap(State.InFate, Addon.InFate());

        var target = TargetManager.Target;
        // Enemy Target
        UpdateStateMap(State.EnemyTarget, target?.ObjectKind == ObjectKind.BattleNpc);

        // Player Target
        UpdateStateMap(State.PlayerTarget, target?.ObjectKind == ObjectKind.Player);

        // NPC Target
        UpdateStateMap(State.NPCTarget, target?.ObjectKind == ObjectKind.EventNpc);

        // Crafting
        UpdateStateMap(State.Crafting, Condition[ConditionFlag.Crafting]);

        // Gathering
        UpdateStateMap(State.Gathering, Condition[ConditionFlag.Gathering]);

        // Mounted
        UpdateStateMap(State.Mounted, Condition[ConditionFlag.Mounted] || Condition[ConditionFlag.Mounted2]);

        // Duty
        var boundByDuty = Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.BoundByDuty56] || Condition[ConditionFlag.BoundByDuty95];
        UpdateStateMap(State.Duty, !inIslandSanctuary && boundByDuty);

        foreach (Element element in Enum.GetValues(typeof(Element)))
        {
            var addonNames = ElementUtil.GetAddonName(element);
            foreach (var addonName in addonNames)
            {
                var addonPointer = Plugin.GameGui.GetAddonByName(addonName);
                if (addonPointer != nint.Zero)
                {
                    // Cast to AtkUnitBase* (requires an unsafe context)
                    var addon = (AtkUnitBase*)addonPointer;
                    if (Addon.IsMouseHovering(addon))
                    {
                        hoverDetected = true;
                        break;
                    }
                }
            }
            if (hoverDetected)
                break;
        }

        UpdateStateMap(State.Hover, hoverDetected);

        // Only update display state if a state has changed.
        //if (StateChanged || HasIdled || Addon.HasAddonStateChanged("HudLayout"))
        //{
        //    UpdateAddonVisibility();

        //    // Always set Idled to false to prevent looping
        //    HasIdled = false;

        //    // Only start idle timer if there was a state change
        //    if(StateChanged && Config.DefaultDelayEnabled)
        //    {
        //        // If idle transition is enabled reset the idle state and start the timer.
        //        IdleTimer.Stop();
        //        IdleTimer.Interval = Config.DefaultDelay;
        //        IdleTimer.Start();
        //    }
        //}
        UpdateAddonOpacity();
    }

    private void UpdateStateMap(State state, bool value)
    {
        if (StateMap[state] == value)
            return;

        StateMap[state] = value;
        StateChanged = true;
    }

    private void UpdateAddonVisibility()
    {
        UpdateAddonVisibility(false);
    }

    private void UpdateAddonVisibility(bool forceShow)
    {
        if(!IsSafeToWork())
            return;

        forceShow = !Enabled || forceShow || Addon.IsHudManagerOpen();

        foreach(var element in Enum.GetValues<Element>())
        {
            var addonNames = ElementUtil.GetAddonName(element);
            if(addonNames.Length == 0)
                continue;

            var setting = Setting.Unknown;
            if(forceShow)
                setting = Setting.Show;

            if(setting == Setting.Unknown)
            {
                var elementConfig = Config.GetElementConfig(element);

                var selected = elementConfig.FirstOrDefault(entry => StateMap[entry.state]);
                if (selected is not null && (selected.state != State.Default || !Config.DefaultDelayEnabled || HasIdled))
                    setting = selected.setting;
            }

            if(setting == Setting.Unknown)
                continue;

            foreach(var addonName in addonNames)
                Addon.SetAddonVisibility(addonName, setting == Setting.Show);
        }
    }
    private float MoveTowards(float current, float target, float maxDelta)
    {
        if (Math.Abs(target - current) <= maxDelta)
            return target;
        return current + Math.Sign(target - current) * maxDelta;
    }
    private unsafe bool IsAddonHovered(string addonName)
    {
        var addonPointer = GameGui.GetAddonByName(addonName);
        if (addonPointer == nint.Zero)
            return false;

        var addon = (AtkUnitBase*)addonPointer;
        var mousePos = ImGui.GetMousePos();
        float posX = addon->GetX();
        float posY = addon->GetY();
        float width = addon->GetScaledWidth(true);
        float height = addon->GetScaledHeight(true);

        return mousePos.X >= posX && mousePos.X <= posX + width &&
               mousePos.Y >= posY && mousePos.Y <= posY + height;
    }
    private void UpdateAddonOpacity()
    {
        foreach (Element element in Enum.GetValues(typeof(Element)))
        {
            if (element.ShouldIgnoreElement())
                continue;

            var addonNames = ElementUtil.GetAddonName(element);
            if (addonNames.Length == 0)
                continue;

            // Retrieve configuration entries for this element.
            var elementConfig = Config.GetElementConfig(element);
            // Try to find an active config entry; if none found, fall back to the Default entry.
            ConfigEntry? selectedEntry = elementConfig.FirstOrDefault(e => StateMap[e.state])
                                          ?? elementConfig.FirstOrDefault(e => e.state == State.Default);

            // Determine the effective setting.
            // If the plugin is disabled or the HUD Manager is open, force Show.
            Setting effectiveSetting = Setting.Unknown;
            if (!Enabled || Addon.IsHudManagerOpen())
            {
                effectiveSetting = Setting.Show;
            }
            else if (selectedEntry != null && (selectedEntry.state != State.Default || !Config.DefaultDelayEnabled || HasIdled))
            {
                effectiveSetting = selectedEntry.setting;
            }
            else
            {
                effectiveSetting = Setting.Hide;
            }
            if (effectiveSetting == Setting.Unknown)
                continue;

            // Process each addon name associated with this element.
            foreach (var addonName in addonNames)
            {
                // Retrieve current alpha (or fallback if not present).
                if (!CurrentAlphas.TryGetValue(addonName, out float currentAlpha))
                    currentAlpha = Config.DefaultAlpha;

                float targetAlpha = Config.DefaultAlpha; // default fallback

                if (selectedEntry != null)
                {
                    if (selectedEntry.state == State.Hover)
                    {
                        // For Hover: only update target opacity if this addon is hovered.
                        // Otherwise, leave it at its current alpha.
                        targetAlpha = IsAddonHovered(addonName) ? selectedEntry.Opacity : currentAlpha;
                    }
                    else
                    {
                        // For non-hover states, if the effective setting is Show, use the configured opacity;
                        // if Hide, fade out (target 0).
                        targetAlpha = (effectiveSetting == Setting.Show) ? selectedEntry.Opacity : 0.0f;
                    }
                }

                // Smoothly interpolate current opacity toward the target.
                currentAlpha = MoveTowards(currentAlpha, targetAlpha, Config.TransitionSpeed * (float)Framework.UpdateDelta.TotalSeconds);
                CurrentAlphas[addonName] = currentAlpha;
                Addon.SetAddonOpacity(addonName, currentAlpha);

                // Handle visibility using old functionality:
                // If effective setting is Show, ensure the addon is visible.
                // If Hide, once opacity is nearly zero, hide it by moving off-screen.
                if (effectiveSetting == Setting.Show)
                {
                    Addon.SetAddonVisibility(addonName, true);
                }
                else
                {
                    if (currentAlpha < 0.05f)
                        Addon.SetAddonVisibility(addonName, false);
                    else
                        Addon.SetAddonVisibility(addonName, true);
                }
            }
        }
    }








    /// <summary>
    /// Returns whether it is safe for the plugin to perform work,
    /// dependent on whether the game is on a login or loading screen.
    /// </summary>
    private bool IsSafeToWork()
    {
        return !Condition[ConditionFlag.BetweenAreas] && ClientState.IsLoggedIn;
    }
}