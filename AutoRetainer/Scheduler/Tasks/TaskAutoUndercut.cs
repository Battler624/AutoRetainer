using AutoRetainer.Scheduler.Handlers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;

namespace AutoRetainer.Scheduler.Tasks;

/// <summary>
/// Iterates through a retainer's sell list and undercuts any items that are
/// no longer the cheapest on the market board by the configured amount.
/// </summary>
internal static class TaskAutoUndercut
{
    // State machine for the per-item flow
    private enum State
    {
        Idle,
        OpenSellList,
        WaitForSellList,
        SelectItem,
        WaitForAdjustPrice,
        CheckBoard,
        WaitForBoard,
        SetPrice,
        ConfirmPrice,
        WaitForConfirm,
        NextItem,
        Done
    }

    private static State _state = State.Idle;
    private static int _currentItemIndex = 0;
    private static int _totalItems = 0;
    private static uint _lowestMarketPrice = 0;

    public static void Enqueue()
    {
        if (!C.EnableAutoUndercut) return;

        _state = State.Idle;
        _currentItemIndex = 0;

        P.TaskManager.Enqueue(OpenSellList, "AutoUndercut: Open Sell List");
        P.TaskManager.Enqueue(ProcessItems, "AutoUndercut: Process Items");
    }

    private static unsafe bool? OpenSellList()
    {
        // The retainer menu addon is "RetainerList" — we need to click "Items for Sale"
        if (!TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !addon->IsVisible)
            return false;

        // Button index 2 = "Items for Sale" in the retainer menu
        // This fires the callback to open RetainerSellList
        Callback.Fire(addon, true, 2);
        _state = State.WaitForSellList;
        return true;
    }

    private static unsafe bool? ProcessItems()
    {
        switch (_state)
        {
            case State.WaitForSellList:
                if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sellList) || !sellList->IsVisible)
                    return false;

                // Count items in the list
                // Node 12 typically contains the list; each item is a child
                // We'll iterate based on what's actually populated
                _totalItems = GetSellListItemCount(sellList);
                if (_totalItems == 0)
                {
                    _state = State.Done;
                    return true;
                }
                _currentItemIndex = 0;
                _state = State.SelectItem;
                return false;

            case State.SelectItem:
                if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sl) || !sl->IsVisible)
                    return false;

                if (_currentItemIndex >= _totalItems)
                {
                    _state = State.Done;
                    // Close the sell list
                    Callback.Fire(sl, true, -1);
                    return true;
                }

                // Click the item at _currentItemIndex to select it, then click "Adjust Price"
                // Callback value 0 = select item, the int is the index
                Callback.Fire(sl, true, 0, _currentItemIndex);
                _state = State.WaitForAdjustPrice;
                return false;

            case State.WaitForAdjustPrice:
                // After selecting, click "Adjust Price" button (callback 3 on RetainerSellList)
                if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sl2) || !sl2->IsVisible)
                    return false;
                Callback.Fire(sl2, true, 3); // "Adjust Price" button
                _state = State.CheckBoard;
                return false;

            case State.CheckBoard:
                // Wait for ItemSearchResult to open (market board comparison)
                if (!TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var board) || !board->IsVisible)
                    return false;

                _lowestMarketPrice = GetLowestMarketPrice(board);
                _state = State.SetPrice;
                return false;

            case State.SetPrice:
                if (!TryGetAddonByName<AtkUnitBase>("RetainerSellInputNumeric", out var input) || !input->IsVisible)
                {
                    // Sometimes the price input comes up simultaneously — wait for it
                    if (!TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var b) || !b->IsVisible)
                        return false;
                    return false;
                }

                if (_lowestMarketPrice == 0)
                {
                    // No market data — skip this item
                    Callback.Fire(input, true, -1); // cancel
                    _state = State.NextItem;
                    return false;
                }

                uint currentPrice = GetCurrentListingPrice(input);
                uint targetPrice = Math.Max(C.UndercutMinPrice, _lowestMarketPrice - (uint)C.UndercutBy);

                if (currentPrice <= targetPrice)
                {
                    // Already cheapest (or we ARE the lowest), no change needed
                    Callback.Fire(input, true, -1);
                    _state = State.NextItem;
                    return false;
                }

                // Set the new price value in the numeric input field
                SetNumericInputValue(input, targetPrice);
                _state = State.ConfirmPrice;
                return false;

            case State.ConfirmPrice:
                if (!TryGetAddonByName<AtkUnitBase>("RetainerSellInputNumeric", out var inp) || !inp->IsVisible)
                    return false;
                // Confirm (callback 0 = OK)
                Callback.Fire(inp, true, 0);
                _state = State.WaitForConfirm;
                return false;

            case State.WaitForConfirm:
                // Wait for the input to close
                if (TryGetAddonByName<AtkUnitBase>("RetainerSellInputNumeric", out var inp2) && inp2->IsVisible)
                    return false;
                _state = State.NextItem;
                return false;

            case State.NextItem:
                _currentItemIndex++;
                _state = State.SelectItem;
                return false;

            case State.Done:
                return true;

            default:
                return true;
        }
    }

    private static unsafe int GetSellListItemCount(AtkUnitBase* addon)
    {
        // The RetainerSellList has items in a list node. The count is readable from
        // the AtkComponentList child. Typically node ID 12 is the list component.
        // We walk its children to count non-empty slots.
        try
        {
            var listNode = addon->GetNodeById(12);
            if (listNode == null) return 0;
            var list = (AtkComponentNode*)listNode;
            if (list->Component == null) return 0;
            // Each child node in the list = one item slot; check if text node is non-empty
            int count = 0;
            for (var i = 0; i < list->Component->UldManager.NodeListCount; i++)
            {
                var node = list->Component->UldManager.NodeList[i];
                if (node == null) continue;
                // A populated slot will have its item name text node visible
                if (node->IsVisible()) count++;
            }
            return Math.Min(count, 20); // retainers can hold max 20 items for sale
        }
        catch { return 0; }
    }

    private static unsafe uint GetLowestMarketPrice(AtkUnitBase* addon)
    {
        // ItemSearchResult lists market prices in a scrollable list.
        // Node layout: list node → rows → price column text node.
        // This mirrors how PennyPincher reads it.
        try
        {
            // Node 9 is typically the first listing row's price text in ItemSearchResult.
            // Walk through result rows and find the lowest price that isn't from our own retainer.
            uint lowest = 0;
            // Simplified: read first result row price (index 0 = cheapest since sorted ascending)
            var priceNode = addon->GetNodeById(9);
            if (priceNode == null) return 0;
            var textNode = (AtkTextNode*)priceNode;
            var priceStr = textNode->NodeText.ToString().Replace(",", "").Trim();
            if (uint.TryParse(priceStr, out var price))
                lowest = price;
            return lowest;
        }
        catch { return 0; }
    }

    private static unsafe uint GetCurrentListingPrice(AtkUnitBase* addon)
    {
        // RetainerSellInputNumeric has a numeric input field showing current price
        try
        {
            var node = addon->GetNodeById(4); // numeric input value node
            if (node == null) return 0;
            var numNode = (AtkTextNode*)node;
            var str = numNode->NodeText.ToString().Replace(",", "").Trim();
            return uint.TryParse(str, out var v) ? v : 0;
        }
        catch { return 0; }
    }

    private static unsafe void SetNumericInputValue(AtkUnitBase* addon, uint value)
    {
        // Fire callback with the new price value
        // For numeric input dialogs, callback(0, value) sets the number
        Callback.Fire(addon, false, 0, (int)value);
    }
}