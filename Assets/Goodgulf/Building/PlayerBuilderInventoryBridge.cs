using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Acts as a bridge between the player and the builder system,
/// exposing a simple inventory API for buildable items.
/// 
/// Inventory is stored as:
///     Key   = buildItemId
///     Value = amount available
/// </summary>
public class PlayerBuilderInventoryBridge : MonoBehaviour
{
    /// <summary>
    /// Holds all buildable items and their remaining amounts.
    /// </summary>
    public Dictionary<int, int> Inventory;

    /// <summary>
    /// Enables verbose debug logging for inventory operations.
    /// </summary>
    [SerializeField] 
    private bool _debugEnabled = true;

    /// <summary>
    /// Unity lifecycle method.
    /// Initializes the inventory dictionary.
    /// </summary>
    void Awake()
    {
        Inventory = new Dictionary<int, int>();
    }

    /// <summary>
    /// Unity lifecycle method.
    /// Adds some test data to the inventory on startup.
    /// Typically removed or replaced in production.
    /// </summary>
    void Start()
    {
        AddItemToInventory(0, 20);
        AddItemToInventory(1, 100);
        AddItemToInventory(2, 10);
    }

    /// <summary>
    /// Checks whether at least one item of the given buildItemId
    /// is available in the inventory.
    /// </summary>
    /// <param name="buildItemId">The ID of the buildable item</param>
    /// <returns>True if at least one item is available, otherwise false</returns>
    public bool InventoryLeft(int buildItemId)
    {
        if (Inventory.ContainsKey(buildItemId))
        {
            int amount = Inventory[buildItemId];

            if (_debugEnabled)
                Debug.Log($"PlayerBuilderInventoryBridge.InventoryLeft({buildItemId}): {amount} left");

            // Return true only if there is at least one item left
            if (amount > 0)
                return true;
        }

        if (_debugEnabled)
            Debug.Log($"PlayerBuilderInventoryBridge.InventoryLeft({buildItemId}): no such item in inventory");

        return false;
    }

    /// <summary>
    /// Returns the remaining amount of a given buildable item.
    /// </summary>
    /// <param name="buildItemId">The ID of the buildable item</param>
    /// <returns>The remaining amount, or 0 if not found</returns>
    public int InventoryLeftAmount(int buildItemId)
    {
        if (Inventory.ContainsKey(buildItemId))
        {
            return Inventory[buildItemId];
        }

        if (_debugEnabled)
            Debug.Log($"PlayerBuilderInventoryBridge.InventoryLeftAmount({buildItemId}): no such item in inventory");

        return 0;
    }

    /// <summary>
    /// Attempts to consume a specific amount of an item from the inventory.
    /// </summary>
    /// <param name="buildItemId">The ID of the buildable item</param>
    /// <param name="amount">The amount to consume</param>
    /// <returns>
    /// True if the inventory contained enough items and the amount was consumed,
    /// otherwise false.
    /// </returns>
    public bool ConsumeItemAmount(int buildItemId, int amount)
    {
        if (Inventory.ContainsKey(buildItemId))
        {
            int amountInInventry = Inventory[buildItemId];

            if (_debugEnabled)
                Debug.Log($"PlayerBuilderInventoryBridge.ConsumeItemAmount({buildItemId},{amount}): amount in inventory={amountInInventry}");

            // Only consume if enough items are available
            if (amount <= amountInInventry)
            {
                amountInInventry -= amount;
                Inventory[buildItemId] = amountInInventry;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds an amount of an item to the inventory.
    /// Creates a new entry if the item does not yet exist.
    /// </summary>
    /// <param name="buildItemId">The ID of the buildable item</param>
    /// <param name="amount">The amount to add</param>
    /// <returns>The new total amount of this item in the inventory</returns>
    public int AddItemToInventory(int buildItemId, int amount)
    {
        if (Inventory.ContainsKey(buildItemId))
        {
            int amountInInventry = Inventory[buildItemId];
            amountInInventry += amount;
            Inventory[buildItemId] = amountInInventry;

            if (_debugEnabled)
                Debug.Log($"PlayerBuilderInventoryBridge.AddItemToInventory({buildItemId},{amount}): new amount in inventory={amountInInventry}");

            return amountInInventry;
        }
        else
        {
            if (_debugEnabled)
                Debug.Log($"PlayerBuilderInventoryBridge.AddItemToInventory({buildItemId},{amount}): new item amount");

            Inventory.Add(buildItemId, amount);
            return amount;
        }
    }
}
