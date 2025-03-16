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
    private readonly Dictionary<string, Element> _addonNameToElement = new();

    // Opacity Management
    private readonly Dictionary<string, float> _currentAlphas = new();
    private readonly Dictionary<string, bool> _finishingHover = new();

    // Commands
    private const string CommandName = "/pfader";
    private bool _enabled = true;

    // Territory Excel sheet.
    private readonly ExcelSheet<TerritoryType> _territorySheet;

    // Delay management Utility
    private readonly DelayManager _delayManager = new DelayManager();

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

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsSafeToWork())
            return;

        _stateChanged = false;
        UpdateInputStates();

        UpdateMouseHoverState();

        UpdateAddonOpacity();
    }


    #region Input & State Management

    private void UpdateInputStates()
    {
        // Update states based on key, chat, movement, combat, etc.
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
        UpdateHoverStates();
        bool hoverDetected = _addonHoverStates.Values.Any(hovered => hovered);
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

        if (!Config.DefaultDelayEnabled)
            _delayManager.ClearAll();

        foreach (var addonName in _addonNameToElement.Keys)
        {
            // Retrieve the associated element for config purposes.
            var element = _addonNameToElement[addonName];
            var elementConfig = Config.GetElementConfig(element);
            bool isHovered = _addonHoverStates.TryGetValue(addonName, out bool hovered) && hovered;

            ConfigEntry candidate = GetCandidateConfig(addonName, elementConfig, isHovered);
            Setting effectiveSetting = GetEffectiveSetting(candidate);

            float currentAlpha = _currentAlphas.TryGetValue(addonName, out var alpha) ? alpha : Config.DefaultAlpha;
            float targetAlpha = CalculateTargetAlpha(candidate, effectiveSetting, isHovered, currentAlpha);

            bool isHoverState = (candidate.state == State.Hover);

            if (isHovered || (_finishingHover.TryGetValue(addonName, out bool finishing) && finishing))
            {
                if (isHoverState)
                    _finishingHover[addonName] = true;

                if (currentAlpha < candidate.Opacity - 0.001f)
                {
                    isHoverState = true;
                    targetAlpha = candidate.Opacity;
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
        // If hovered, try to get the Hover entry.
        ConfigEntry? candidate = isHovered
            ? elementConfig.FirstOrDefault(e => e.state == State.Hover)
            : null;

        // If no hover candidate, choose a non-hover active state or fallback to default.
        if (candidate == null)
        {
            candidate = elementConfig.FirstOrDefault(e => _stateMap[e.state] && e.state != State.Hover)
                        ?? elementConfig.FirstOrDefault(e => e.state == State.Default);
        }
        var now = DateTime.Now;
        // If non-default, record state for delay purposes.
        if (candidate != null && candidate.state != State.Default)
        {
            _delayManager.RecordNonDefaultState(addonName, candidate, now);

            // **Force non-default states to Show** if they are Hide in config ( should only be relevant for existing configs )
            if (candidate.setting == Setting.Hide)
            {
                candidate.setting = Setting.Show;
            }
        }
        // If default and delay is enabled, use the delayed state if within the delay period.
        else if (candidate != null && candidate.state == State.Default && Config.DefaultDelayEnabled)
        {
            candidate = _delayManager.GetDelayedConfig(addonName, candidate, now, Config.DefaultDelay);
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


    /// <summary>
    /// Checks if it is safe for the plugin to perform work.
    /// </summary>
    private bool IsSafeToWork() => !Condition[ConditionFlag.BetweenAreas] && ClientState.IsLoggedIn;
}
