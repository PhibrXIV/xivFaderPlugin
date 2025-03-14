using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace FaderPlugin;

public static unsafe class Addon
{
    private static readonly AtkStage* Stage = AtkStage.Instance();

    private static readonly Dictionary<string, (short, short)> StoredPositions = new();
    private static readonly Dictionary<string, bool> LastState = new();

    private static bool IsAddonOpen(string name)
    {
        var addonPointer = Plugin.GameGui.GetAddonByName(name);
        return addonPointer != nint.Zero;
    }

    public static bool HasAddonStateChanged(string name)
    {
        var currentState = IsAddonOpen(name);
        var changed = !LastState.ContainsKey(name) || LastState[name] != currentState;

        LastState[name] = currentState;

        return changed;
    }

    private static bool IsAddonFocused(string name)
    {
        foreach (var addon in Stage->RaptureAtkUnitManager->AtkUnitManager.FocusedUnitsList.Entries)
        {
            if (addon.Value == null || addon.Value->Name == null)
                continue;

            if (name.Equals(addon.Value->NameString))
                return true;
        }

        return false;
    }

    public static bool IsHudManagerOpen()
    {
        return IsAddonOpen("HudLayout");
    }

    public static bool IsChatFocused()
    {
        // Check for ChatLogPanel_[0-3] as well to prevent chat from disappearing while user is scrolling through logs via controller input
        return IsAddonFocused("ChatLog")
               || IsAddonFocused("ChatLogPanel_0")
               || IsAddonFocused("ChatLogPanel_1")
               || IsAddonFocused("ChatLogPanel_2")
               || IsAddonFocused("ChatLogPanel_3");
    }

    public static unsafe bool IsMouseHovering(AtkUnitBase* addon)
    {
        // Get the addon’s screen position and size.
        float posX = addon->GetX();
        float posY = addon->GetY();
        float width = addon->GetScaledWidth(true);
        float height = addon->GetScaledHeight(true);

        // Retrieve the current mouse position (using ImGui here, or another Dalamud method).
        var mousePos = ImGui.GetMousePos();

        return mousePos.X >= posX && mousePos.X <= posX + width &&
               mousePos.Y >= posY && mousePos.Y <= posY + height;
    }
    public static unsafe void SetAddonOpacity(string addonName, float alpha)
    {
        // Get the addon pointer by name.
        var addonPointer = Plugin.GameGui.GetAddonByName(addonName);
        if (addonPointer == nint.Zero)
            return;

        var addon = (AtkUnitBase*)addonPointer;
        // Check if there are any nodes.
        if (addon->UldManager.NodeListCount <= 0)
            return;

        // Target the first node in the list.
        AtkResNode* node = addon->UldManager.NodeList[0];
        if (node == null)
            return;

        // Read the current color as a ByteColor.
        ByteColor currentColor = node->Color;
        byte r = currentColor.R;
        byte g = currentColor.G;
        byte b = currentColor.B;
        // Calculate the new alpha value.
        byte newAlpha = (byte)(alpha * 255);

        // Create a new ByteColor and assign its fields.
        ByteColor newColor = default;
        newColor.A = newAlpha;
        newColor.R = r;
        newColor.G = g;
        newColor.B = b;
        node->Color = newColor;
    }

public static bool AreHotbarsLocked()
    {
        var hotbar = Plugin.GameGui.GetAddonByName("_ActionBar");
        var crossbar = Plugin.GameGui.GetAddonByName("_ActionCross");

        if (hotbar == nint.Zero || crossbar == nint.Zero)
            return true;

        var hotbarAddon = (AddonActionBar*)hotbar;
        var crossbarAddon = (AddonActionCross*)hotbar;

        try
        {
            // Check whether Mouse Mode or Gamepad Mode is enabled.
            var mouseModeEnabled = hotbarAddon->ShowHideFlags == 0;
            return mouseModeEnabled ? hotbarAddon->IsLocked : crossbarAddon->IsLocked;
        }
        catch(AccessViolationException)
        {
            return true;
        }
    }

    public static void SetAddonVisibility(string name, bool isVisible)
    {
        var addonPointer = Plugin.GameGui.GetAddonByName(name);
        if(addonPointer == nint.Zero)
            return;

        var addon = (AtkUnitBase*)addonPointer;

        if(isVisible)
        {
            // Restore the element position on screen.
            if (StoredPositions.TryGetValue(name, out var position) && (addon->X == -9999 || addon->Y == -9999))
            {
                var (x, y) = position;
                addon->SetPosition(x, y);
            }
        }
        else
        {
            // Store the position before hiding the element.
            if(addon->X != -9999 && addon->Y != -9999)
                StoredPositions[name] = (addon->X, addon->Y);

            // Move the element off screen so it can't be interacted with.
            addon->SetPosition(-9999, -9999);
        }
    }

    public record AddonPosition(bool IsPresent, short X, short Y, short W, short H)
    {
        public Vector2 Start => new Vector2(X, Y);
        public Vector2 End => new Vector2(X + W, Y + H);

        public static AddonPosition Empty => new(false, 0, 0, 0, 0);
    }

    public static AddonPosition GetAddonPosition(string name)
    {
        var addonPointer = Plugin.GameGui.GetAddonByName(name);
        if(addonPointer == nint.Zero)
            return AddonPosition.Empty;

        var addon = (AtkUnitBase*)addonPointer;

        var width = (short) addon->GetScaledWidth(true);
        var height = (short) addon->GetScaledHeight(true);
        return new AddonPosition(true, addon->X, addon->Y, width, height);
    }

    public static bool IsWeaponUnsheathed()
    {
        return UIState.Instance()->WeaponState.IsUnsheathed;
    }

    public static bool InSanctuary()
    {
        return TerritoryInfo.Instance()->InSanctuary;
    }

    public static bool InFate()
    {
        return FateManager.Instance()->CurrentFate != null;
    }

    public static bool IsMoving()
    {
        return AgentMap.Instance()->IsPlayerMoving != 0;
    }
}