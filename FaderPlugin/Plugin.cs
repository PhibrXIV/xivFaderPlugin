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
using FaderPlugin.Utils;

namespace FaderPlugin;

public class Plugin : IDalamudPlugin
{
    // Plugin services
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

    // Configuration and windows.
    public readonly Configuration Config;
    private readonly WindowSystem _windowSystem = new("Fader");
    private readonly ConfigWindow _configWindow;

    // State maps and timers.
    private readonly Dictionary<State, bool> _stateMap = new();
    private bool _stateChanged;
    private readonly Timer _idleTimer = new();
    private bool _hasIdled;
    private readonly Timer _chatActivityTimer = new();
    private bool _hasChatActivity;

    // Commands and opacity state.
    private const string CommandName = "/pfader";
    private bool _enabled = true;
    private readonly Dictionary<string, float> _currentAlphas = new();

    // Territory Excel sheet.
    private readonly ExcelSheet<TerritoryType> _territorySheet;

    // Delay management is now handled by a separate utility class.
    private readonly DelayManager _delayManager = new DelayManager();

    public Plugin()
    {
        LoadConfig(out Config);
        LanguageChanged(PluginInterface.UiLanguage);

        _configWindow = new ConfigWindow(this);
        _windowSystem.AddWindow(_configWindow);

        _territorySheet = Data.GetExcelSheet<TerritoryType>();

        // Subscribe to events.
        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(FaderCommandHandler)
        {
            HelpMessage = "Opens settings\n't' toggles whether it's enabled.\n'on' enables the plugin\n'off' disables the plugin."
        });

        // Initialise state map.
        foreach (State state in Enum.GetValues(typeof(State)))
            _stateMap[state] = state == State.Default;

        // Idle timer: fire only once when idling.
        _idleTimer.AutoReset = false;
        _idleTimer.Elapsed += (_, _) => _hasIdled = true;
        _idleTimer.Start();

        // Chat activity timer.
        _chatActivityTimer.Elapsed += (_, _) => _hasChatActivity = false;
        ChatGui.ChatMessage += OnChatMessage;
        PluginInterface.LanguageChanged += LanguageChanged;

        // Recovery: set default delay if misconfigured.
        if (Config.DefaultDelay == 0)
            Config.DefaultDelay = 2000;
    }

    public void Dispose()
    {
        // Clean up (unhide all elements)
        ForceShowAllElements();

        PluginInterface.LanguageChanged -= LanguageChanged;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        ChatGui.ChatMessage -= OnChatMessage;

        _idleTimer.Dispose();
        _chatActivityTimer.Dispose();
        _configWindow.Dispose();
        _windowSystem.RemoveWindow(_configWindow);
    }

    #region Language & Config Loading

    private void LanguageChanged(string langCode)
    {
        Language.Culture = new CultureInfo(langCode);
    }

    private void LoadConfig(out Configuration configuration)
    {
        var existingConfig = PluginInterface.GetPluginConfig();
        configuration = (existingConfig is { Version: 6 })
            ? (Configuration)existingConfig
            : new Configuration();
        configuration.Initialize();
    }

    #endregion

    #region UI Drawing

    private void DrawUi() => _windowSystem.Draw();

    private void DrawConfigUi() => _configWindow.Toggle();

    #endregion

    #region Command Handling

    private void FaderCommandHandler(string s, string arguments)
    {
        switch (arguments.Trim())
        {
            case "t" or "toggle":
                _enabled = !_enabled;
                ChatGui.Print(_enabled ? Language.ChatPluginEnabled : Language.ChatPluginDisabled);
                break;
            case "on":
                _enabled = true;
                ChatGui.Print(Language.ChatPluginEnabled);
                break;
            case "off":
                _enabled = false;
                ChatGui.Print(Language.ChatPluginDisabled);
                break;
            case "":
                _configWindow.Toggle();
                break;
        }
    }

    #endregion

    #region Chat & Update Event Handlers

    private void OnChatMessage(XivChatType type, int _, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        // Only trigger on specific chat types.
        if (!Constants.ActiveChatTypes.Contains(type)
            && (!Config.ImportantActivity || !Constants.ImportantChatTypes.Contains(type))
            && (!Config.EmoteActivity || !Constants.EmoteChatTypes.Contains(type)))
            return;

        _hasChatActivity = true;
        _chatActivityTimer.Stop();
        _chatActivityTimer.Interval = Config.ChatActivityTimeout;
        _chatActivityTimer.Start();
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsSafeToWork())
            return;

        _stateChanged = false;
        UpdateInputStates();
        UpdateMouseHoverState();

        UpdateAddonOpacity();
    }

    #endregion

    #region Input & State Management

    private void UpdateInputStates()
    {
        // Update states based on key, chat, movement, combat, etc.
        UpdateState(State.UserFocus, KeyState[Config.OverrideKey] || (Config.FocusOnHotbarsUnlock && !Addon.AreHotbarsLocked()));
        UpdateState(State.AltKeyFocus, KeyState[(int)Constants.OverrideKeys.Alt]);
        UpdateState(State.CtrlKeyFocus, KeyState[(int)Constants.OverrideKeys.Ctrl]);
        UpdateState(State.ShiftKeyFocus, KeyState[(int)Constants.OverrideKeys.Shift]);
        UpdateState(State.ChatFocus, Addon.IsChatFocused());
        UpdateState(State.ChatActivity, _hasChatActivity);
        UpdateState(State.IsMoving, Addon.IsMoving());
        UpdateState(State.Combat, Condition[ConditionFlag.InCombat]);
        UpdateState(State.WeaponUnsheathed, Addon.IsWeaponUnsheathed());
        UpdateState(State.InSanctuary, Addon.InSanctuary());
        UpdateState(State.InFate, Addon.InFate());

        var target = TargetManager.Target;
        UpdateState(State.EnemyTarget, target?.ObjectKind == ObjectKind.BattleNpc);
        UpdateState(State.PlayerTarget, target?.ObjectKind == ObjectKind.Player);
        UpdateState(State.NPCTarget, target?.ObjectKind == ObjectKind.EventNpc);
        UpdateState(State.Crafting, Condition[ConditionFlag.Crafting]);
        UpdateState(State.Gathering, Condition[ConditionFlag.Gathering]);
        UpdateState(State.Mounted, Condition[ConditionFlag.Mounted] || Condition[ConditionFlag.Mounted2]);

        bool inIslandSanctuary = _territorySheet.HasRow(ClientState.TerritoryType)
            && _territorySheet.GetRow(ClientState.TerritoryType).TerritoryIntendedUse.RowId == 49;
        UpdateState(State.IslandSanctuary, inIslandSanctuary);

        var boundByDuty = Condition[ConditionFlag.BoundByDuty]
                          || Condition[ConditionFlag.BoundByDuty56]
                          || Condition[ConditionFlag.BoundByDuty95];
        UpdateState(State.Duty, !inIslandSanctuary && boundByDuty);
    }

    private unsafe void UpdateMouseHoverState()
    {
        // Check all element addons for mouse hover.
        bool hoverDetected = false;
        foreach (Element element in Enum.GetValues(typeof(Element)))
        {
            var addonNames = ElementUtil.GetAddonName(element);
            foreach (var addonName in addonNames)
            {
                var addonPointer = GameGui.GetAddonByName(addonName);
                if (addonPointer != nint.Zero)
                {
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
        UpdateState(State.Hover, hoverDetected);
    }

    private void UpdateState(State state, bool value)
    {
        if (_stateMap[state] != value)
        {
            _stateMap[state] = value;
            _stateChanged = true;
        }
    }

    #endregion

    #region Opacity & Visibility Management

    /// <summary>
    /// Update each addon's opacity based on the current states and configuration.
    /// </summary>
    private void UpdateAddonOpacity()
    {
        if (!IsSafeToWork())
            return;

        // Determine if we should force showing elements.
        bool forceShow = !_enabled || Addon.IsHudManagerOpen();

        // When forceShow is active, only update visibility but leave opacities untouched.
        if (forceShow)
        {
            foreach (Element element in Enum.GetValues(typeof(Element)))
            {
                if (element.ShouldIgnoreElement())
                    continue;

                var addonNames = ElementUtil.GetAddonName(element);
                if (addonNames.Length == 0)
                    continue;

                foreach (var addonName in addonNames)
                {
                    // Do not modify _currentAlphas, simply force visibility.
                    Addon.SetAddonVisibility(addonName, true);
                }
            }
            return;
        }

        // Clear delay state if default delay is disabled.
        if (!Config.DefaultDelayEnabled)
            _delayManager.ClearAll();

        var now = DateTime.Now;
        foreach (Element element in Enum.GetValues(typeof(Element)))
        {
            if (element.ShouldIgnoreElement())
                continue;

            var addonNames = ElementUtil.GetAddonName(element);
            if (addonNames.Length == 0)
                continue;

            var elementConfig = Config.GetElementConfig(element);
            foreach (var addonName in addonNames)
            {
                bool isHovered = IsAddonHovered(addonName);
                ConfigEntry candidate = GetCandidateConfig(addonName, elementConfig, now, isHovered);
                Setting effectiveSetting = GetEffectiveSetting(candidate);

                float currentAlpha = _currentAlphas.TryGetValue(addonName, out var alpha) ? alpha : Config.DefaultAlpha;
                float targetAlpha = CalculateTargetAlpha(candidate, effectiveSetting, isHovered, currentAlpha);

                // Smoothly interpolate the opacity.
                currentAlpha = MoveTowards(currentAlpha, targetAlpha, Config.TransitionSpeed * (float)Framework.UpdateDelta.TotalSeconds);
                _currentAlphas[addonName] = currentAlpha;
                Addon.SetAddonOpacity(addonName, currentAlpha);

                // Update visibility based on effective setting and current alpha.
                Addon.SetAddonVisibility(addonName, effectiveSetting == Setting.Show || currentAlpha >= 0.05f);
            }
        }
    }

    /// <summary>
    /// Determine the configuration candidate for an addon given its element config.
    /// </summary>
    private ConfigEntry GetCandidateConfig(string addonName, List<ConfigEntry> elementConfig, DateTime now, bool isHovered)
    {
        // 1. If hovered, try to get the Hover entry.
        ConfigEntry? candidate = isHovered
            ? elementConfig.FirstOrDefault(e => e.state == State.Hover)
            : null;

        // 2. If no hover candidate, choose a non-hover active state or fallback to default.
        if (candidate == null)
        {
            candidate = elementConfig.FirstOrDefault(e => _stateMap[e.state] && e.state != State.Hover)
                        ?? elementConfig.FirstOrDefault(e => e.state == State.Default);
        }

        // 3. If non-default, record state for delay purposes.
        if (candidate != null && candidate.state != State.Default)
        {
            _delayManager.RecordNonDefaultState(addonName, candidate, now);
        }
        // 4. If default and delay is enabled, use the delayed state if within the delay period.
        else if (candidate != null && candidate.state == State.Default && Config.DefaultDelayEnabled)
        {
            candidate = _delayManager.GetDelayedConfig(addonName, candidate, now, Config.DefaultDelay);
        }
        return candidate;
    }

    /// <summary>
    /// Determines the effective setting for an addon.
    /// </summary>
    private Setting GetEffectiveSetting(ConfigEntry candidate)
    {
        if (!_enabled || Addon.IsHudManagerOpen())
            return Setting.Show;
        if (candidate.state != State.Default || !Config.DefaultDelayEnabled || _hasIdled)
            return candidate.setting;
        return Setting.Hide;
    }

    /// <summary>
    /// Computes the target opacity for an addon based on its candidate config and state.
    /// </summary>
    private float CalculateTargetAlpha(ConfigEntry candidate, Setting effectiveSetting, bool isHovered, float currentAlpha)
    {
        if (candidate.state == State.Hover)
        {
            // For hover state, if currently hovered, use the candidate opacity; else, maintain current.
            return isHovered ? candidate.Opacity : currentAlpha;
        }
        return (effectiveSetting == Setting.Show) ? candidate.Opacity : 0.0f;
    }

    /// <summary>
    /// Smoothly moves current value toward target value.
    /// </summary>
    private float MoveTowards(float current, float target, float maxDelta)
    {
        if (Math.Abs(target - current) <= maxDelta)
            return target;
        return current + Math.Sign(target - current) * maxDelta;
    }

    /// <summary>
    /// Checks if the addon identified by addonName is currently hovered.
    /// </summary>
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

    private void ForceShowAllElements()
    {
        foreach (Element element in Enum.GetValues(typeof(Element)))
        {
            if (element.ShouldIgnoreElement())
                continue;

            var addonNames = ElementUtil.GetAddonName(element);
            if (addonNames.Length == 0)
                continue;

            foreach (var addonName in addonNames)
            {
                // Set opacity to maximum (fully visible)
                Addon.SetAddonOpacity(addonName, 1.0f);
                // Force the element to be visible.
                Addon.SetAddonVisibility(addonName, true);
            }
        }
    }


    #endregion

    /// <summary>
    /// Checks if it is safe for the plugin to perform work.
    /// </summary>
    private bool IsSafeToWork() => !Condition[ConditionFlag.BetweenAreas] && ClientState.IsLoggedIn;
}
