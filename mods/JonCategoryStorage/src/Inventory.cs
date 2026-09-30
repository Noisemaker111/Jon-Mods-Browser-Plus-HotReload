using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace JonCategoryStorage
{
    public static class Categories
    {
        public static string Of(ItemClass item)
        {
            if (item == null) return "zzzzz";
            if (item is ItemClassModifier) return "08 Item mods";
            return Rules.Category(item.Name, item.Groups,
                tag => item.HasAnyTags(FastTags<TagGroup.Global>.Parse(tag)), item.IsBlock());
        }
        public static string Of(ItemValue value) { return Of(value == null ? null : value.ItemClass); }
    }

    [HarmonyPatch(typeof(StackSortUtil), "getGroup")]
    public static class SortCategory
    {
        public static bool Prefix(ItemStack __0, ref string __result)
        {
            __result = __0 == null || __0.IsEmpty() ? "zzzzz" : Categories.Of(__0.itemValue);
            return false;
        }
    }

    [HarmonyPatch(typeof(XUiM_LootContainer), "StashItems")]
    public static class StashCategories
    {
        [ThreadStatic] static Stack<HashSet<string>> contexts;

        public static void Prefix(IInventory __2)
        {
            // Snapshot before a transfer. Newly inserted items cannot enable
            // extra categories during the same click.
            var categories = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable<ItemStack> stacks = null;
            if (__2 is ITileEntityLootable loot) stacks = loot.items;
            else if (__2 is Bag bag) stacks = bag.GetSlots();
            else if (__2 is Inventory inventory) stacks = inventory.GetSlots();
            else if (__2 is XUiM_PlayerInventory player) stacks = player.GetAllItemStacks();
            if (stacks != null)
                foreach (var stack in stacks)
                    if (stack != null && !stack.IsEmpty()) categories.Add(Categories.Of(stack.itemValue));
            if (contexts == null) contexts = new Stack<HashSet<string>>();
            contexts.Push(categories);
        }

        public static Exception Finalizer(Exception __exception)
        {
            if (contexts != null && contexts.Count != 0) contexts.Pop();
            return __exception;
        }

        public static bool HasCategory(IInventory destination, ItemValue value)
        {
            return Rules.MatchesDestination(destination.HasItem(value), contexts != null && contexts.Count != 0 ? contexts.Peek() : null, Categories.Of(value));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var hasItem = AccessTools.Method(typeof(IInventory), "HasItem");
            var replacement = AccessTools.Method(typeof(StashCategories), nameof(HasCategory));
            int count = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(hasItem))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    count++;
                }
                yield return instruction;
            }
            if (count != 1) throw new InvalidOperationException("Category stash needs exactly one native HasItem decision; found " + count);
        }
    }
}
