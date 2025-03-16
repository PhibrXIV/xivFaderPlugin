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
using System.Numerics;

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
    private DateTime _lastChatActivity = DateTime.MinValue;
    private Dictionary<string, bool> _addonHoverStates = new Dictionary<string, bool>();
    private HashSet<string> _previousHoveredAddons = new();
    private readonly Dictionary<string, Element> _addonNameToElement = new();
    private DateTime _opacityUpdateEndTime = DateTime.MinValue;
    private bool _opacityUpdateActive = false;

    // Opacity Management
    private readonly Dictionary<string, float> _currentAlphas = new();
    private readonly Dictionary<string, float> _targetAlphas = new();
    private readonly Dictionary<string, bool> _finishingHover = new();

    // Commands
    private const string CommandName = "/pfader";
    private bool _enabled = true;

    // Territory Excel sheet.
    private readonly ExcelSheet<TerritoryType> _territorySheet;

    // Delay management Utility
    private readonly Dictionary<string, DateTime> _delayTimers = new();
    private readonly Dictionary<string, ConfigEntry> _lastNonDefaultEntry = new();

    // Enum Cache
    private static readonly Element[] AllElements = Enum.GetValues<Element>();
    private static readonly State[] AllStates = Enum.GetValues<State>();

    public Plugin()
    {
        LoadConfig(out Config);
        LanguageChanged(PluginInterface.UiLanguage);

        _configWindow = new ConfigWindow(this);
        _windowSystem.AddWindow(_configWindow);

        _territorySheet = Data.GetExcelSheet<TerritoryType>();

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(FaderCommandHandler)
        {
            HelpMessage = "Opens settings\n't' toggles whether it's enabled.\n'on' enables the plugin\n'off' disables the plugin."
        });

        foreach (State state in AllStates)
            _stateMap[state] = state == State.Default;

        foreach (var element in AllElements)
        {
            if (element.ShouldIgnoreElement())
                continue;

            var addonNames = ElementUtil.GetAddonName(element);
            foreach (var addonName in addonNames)
            {
                if (!_addonNameToElement.ContainsKey(addonName))
                    _addonNameToElement.Add(addonName, element);
            }
        }

        ChatGui.ChatMessage += OnChatMessage;
        PluginInterface.LanguageChanged += LanguageChanged;

        // Recover from previous misconfiguration
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

        _configWindow.Dispose();
        _windowSystem.RemoveWindow(_configWindow);
    }


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

    private void DrawUi() => _windowSystem.Draw();

    private void DrawConfigUi() => _configWindow.Toggle();


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

    private void OnChatMessage(XivChatType type, int _, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        // Don't trigger chat for non-standard chat channels.
        if (!Constants.ActiveChatTypes.Contains(type)
            && (!Config.ImportantActivity || !Constants.ImportantChatTypes.Contains(type))
            && (!Config.EmoteActivity || !Constants.EmoteChatTypes.Contains(type)))
            return;

        _lastChatActivity = DateTime.Now;
    }
    private bool IsChatActive() =>
    (DateTime.Now - _lastChatActivity).TotalMilliseconds < Config.ChatActivityTimeout;

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsSafeToWork())
            return;

        _stateChanged = false;
        UpdateInputStates();
        UpdateMouseHoverState();

        if (_stateChanged || !DoAlphasMatch() || AnyDelayExpired())
        {
            UpdateAddonOpacity();
        }
    }

    #region Input & State Management

    private void UpdateInputStates()
    {
        UpdateState(State.UserFocus, KeyState[Config.OverrideKey] || (Config.FocusOnHotbarsUnlock && !Addon.AreHotbarsLocked()));
        UpdateState(State.AltKeyFocus, KeyState[(int)Constants.OverrideKeys.Alt]);
        UpdateState(State.CtrlKeyFocus, KeyState[(int)Constants.OverrideKeys.Ctrl]);
        UpdateState(State.ShiftKeyFocus, KeyState[(int)Constants.OverrideKeys.Shift]);
        UpdateState(State.ChatFocus, Addon.IsChatFocused());
        UpdateState(State.ChatActivity, IsChatActive());
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

        var occupied = Condition[ConditionFlag.Occupied]
          || Condition[ConditionFlag.Occupied30]
          || Condition[ConditionFlag.Occupied33]
          || Condition[ConditionFlag.Occupied38]
          || Condition[ConditionFlag.Occupied39]
          || Condition[ConditionFlag.OccupiedInCutSceneEvent]
          || Condition[ConditionFlag.OccupiedInEvent]
          || Condition[ConditionFlag.OccupiedSummoningBell]
          || Condition[ConditionFlag.OccupiedInQuestEvent];

        UpdateState(State.Occupied, occupied);
    }

    /// <summary>
    /// Collects all addon hover states
    /// </summary>
    private void UpdateHoverStates()
    {
        var mousePos = ImGui.GetMousePos();
        _addonHoverStates.Clear();

        foreach (var addonName in _addonNameToElement.Keys)
        {
            // Compute the hover state once per addon.
            _addonHoverStates[addonName] = IsAddonHovered(addonName, mousePos);
        }
    }


    private void UpdateMouseHoverState()
    {
        // Update the hover states for all addons.
        UpdateHoverStates();

        var currentHovered = new HashSet<string>(
            _addonHoverStates.Where(kvp => kvp.Value).Select(kvp => kvp.Key)
        );

        if (!currentHovered.SetEquals(_previousHoveredAddons))
        {
            _stateChanged = true;
        }

        _previousHoveredAddons = currentHovered;

        bool hoverDetected = currentHovered.Any();
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

    private void UpdateAddonOpacity()
    {
        if (!IsSafeToWork())
            return;

        bool forceShow = !_enabled || Addon.IsHudManagerOpen();

        if (forceShow)
        {
            foreach (var addonName in _addonNameToElement.Keys)
            {
                Addon.SetAddonVisibility(addonName, true);
                _finishingHover[addonName] = false;
            }
            return;
        }

            // If delay is disabled, clear any stored delay state.
            if (!Config.DefaultDelayEnabled)
        {
            _delayTimers.Clear();
            _lastNonDefaultEntry.Clear();
        }

        foreach (var addonName in _addonNameToElement.Keys)
        {
            var element = _addonNameToElement[addonName];
            var elementConfig = Config.GetElementConfig(element);
            bool isHovered = _addonHoverStates.TryGetValue(addonName, out bool hovered) && hovered;

            ConfigEntry candidate = GetCandidateConfig(addonName, elementConfig, isHovered);
            Setting effectiveSetting = GetEffectiveSetting(candidate);

            float currentAlpha = _currentAlphas.TryGetValue(addonName, out var alpha) ? alpha : Config.DefaultAlpha;
            float targetAlpha = CalculateTargetAlpha(candidate, effectiveSetting, isHovered, currentAlpha);

            _targetAlphas[addonName] = targetAlpha;

            bool isHoverState = (candidate.state == State.Hover);

            if (isHovered || (_finishingHover.TryGetValue(addonName, out bool finishing) && finishing))
            {
                if (isHoverState)
                    _finishingHover[addonName] = true;

                if (currentAlpha < candidate.Opacity - 0.001f)
                {
                    isHoverState = true;
                    targetAlpha = candidate.Opacity;
                    _targetAlphas[addonName] = targetAlpha;
                }
                else if (!isHovered)
                {
                    _finishingHover[addonName] = false;
                }
            }
            else
            {
                _finishingHover[addonName] = false;
            }

            float transitionSpeed = (targetAlpha > currentAlpha)
                ? Config.EnterTransitionSpeed
                : Config.ExitTransitionSpeed;

            currentAlpha = MoveTowards(currentAlpha, targetAlpha, transitionSpeed * (float)Framework.UpdateDelta.TotalSeconds);
            _currentAlphas[addonName] = currentAlpha;
            Addon.SetAddonOpacity(addonName, currentAlpha);

            bool defaultDisabled = (candidate.state == State.Default && candidate.setting == Setting.Hide);
            bool hidden = defaultDisabled && currentAlpha < 0.05f;
            Addon.SetAddonVisibility(addonName, !hidden);
        }
    }


    private ConfigEntry GetCandidateConfig(string addonName, List<ConfigEntry> elementConfig, bool isHovered)
    {
        // Prefer Hover state when applicable.
        ConfigEntry? candidate = isHovered
            ? elementConfig.FirstOrDefault(e => e.state == State.Hover)
            : null;

        // Fallback: choose an active non-hover state or default.
        if (candidate == null)
        {
            candidate = elementConfig.FirstOrDefault(e => _stateMap[e.state] && e.state != State.Hover)
                        ?? elementConfig.FirstOrDefault(e => e.state == State.Default);
        }

        var now = DateTime.Now;
        if (candidate != null && candidate.state != State.Default)
        {
            // Record the non-default state with a timestamp.
            _delayTimers[addonName] = now;
            _lastNonDefaultEntry[addonName] = candidate;

            // Force non-default states to Show.
            if (candidate.setting == Setting.Hide)
            {
                candidate.setting = Setting.Show;
            }
        }
        else if (candidate != null && candidate.state == State.Default && Config.DefaultDelayEnabled)
        {
            // Check if there's a recent non-default state that should be used.
            if (_delayTimers.TryGetValue(addonName, out var start) &&
                (now - start).TotalMilliseconds < Config.DefaultDelay)
            {
                if (_lastNonDefaultEntry.TryGetValue(addonName, out var nonDefault))
                {
                    candidate = nonDefault;
                }
            }
            else
            {
                // Delay expired; clear stored values.
                _delayTimers.Remove(addonName);
                _lastNonDefaultEntry.Remove(addonName);
            }
        }

        return candidate!;
    }


    private Setting GetEffectiveSetting(ConfigEntry candidate)
    {
        if (!_enabled || Addon.IsHudManagerOpen())
            return Setting.Show;
        return candidate.setting;
    }

    private float CalculateTargetAlpha(ConfigEntry candidate, Setting effectiveSetting, bool isHovered, float currentAlpha)
    {
        if (candidate.state == State.Hover)
        {
            return isHovered ? candidate.Opacity : currentAlpha;
        }
        return (effectiveSetting == Setting.Show) ? candidate.Opacity : 0.0f;
    }

    /// <summary>
    /// Smoothly moves current value toward target value. TODO: add easing functions perhaps?
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
    private unsafe bool IsAddonHovered(string addonName, Vector2 mousePos)
    {
        var addonPointer = GameGui.GetAddonByName(addonName);
        if (addonPointer == nint.Zero)
            return false;

        var addon = (AtkUnitBase*)addonPointer;
        float posX = addon->GetX();
        float posY = addon->GetY();
        float width = addon->GetScaledWidth(true);
        float height = addon->GetScaledHeight(true);

        return mousePos.X >= posX && mousePos.X <= posX + width &&
               mousePos.Y >= posY && mousePos.Y <= posY + height;
    }

    /// <summary>
    /// Forces all elements to be visible and fully opaque.
    /// </summary>
    private void ForceShowAllElements()
    {
        foreach (var addonName in _addonNameToElement.Keys)
        {
            Addon.SetAddonOpacity(addonName, 1.0f);
            Addon.SetAddonVisibility(addonName, true);
        }
    }


    #endregion

    #region Helper Methods
    private bool DoAlphasMatch()
    {
        // Check if both dictionaries have the same number of entries.
        if (_targetAlphas.Count != _currentAlphas.Count)
            return false;

        foreach (var kvp in _targetAlphas)
        {
            if (!_currentAlphas.TryGetValue(kvp.Key, out float currentAlpha) ||
                Math.Abs(currentAlpha - kvp.Value) > 0.001f)
            {
                return false;
            }
        }
        return true;
    }

    private bool AnyDelayExpired()
    {
        var now = DateTime.Now;
        return _delayTimers.Values.Any(timer => (now - timer).TotalMilliseconds >= Config.DefaultDelay);
    }

    /// <summary>
    /// Checks if it is safe for the plugin to perform work.
    /// </summary>
    private bool IsSafeToWork() => !Condition[ConditionFlag.BetweenAreas] && ClientState.IsLoggedIn;

    #endregion
}
