using System;
using System.Collections.Generic;

namespace JonCoopQoL
{
    // Shared by the actual patches and offline checks. Categories route items;
    // they never change item identity, recipes, stack limits, or progression.
    public static class Rules
    {
        public static string Category(string name, string[] groups, Func<string, bool> tag, bool block)
        {
            name = (name ?? "").ToLowerInvariant();
            if (tag("books") || tag("schematics") || tag("craftingSkillMagazine") || tag("csm") ||
                InGroup(groups, "Books") || InGroup(groups, "BooksOnly") || InGroup(groups, "TCReading") ||
                name.StartsWith("book") || name.StartsWith("skillmagazine") || name.EndsWith("skillmagazine") || name.EndsWith("schematic")) return "01 Books";
            // Planting seeds are block items in the native game.
            if (name.StartsWith("planted") || tag("seed") || name.StartsWith("seed")) return "11 Farming";
            if (block) return "12 Building";
            if (tag("medical") || tag("medicine") || name.StartsWith("medical") || name.StartsWith("drug")) return "02 Medicine";
            if (tag("food") || tag("drink") || name.StartsWith("food") || name.StartsWith("drink")) return "03 Food and drink";
            if (tag("ammo") || tag("ammunition") || name.StartsWith("ammo")) return "04 Ammunition";
            if (tag("tool")) return "05 Tools";
            if (tag("weapon") || name.StartsWith("gun") || name.StartsWith("melee")) return "06 Weapons";
            if (tag("armor") || tag("clothing") || name.StartsWith("armor") || name.StartsWith("apparel")) return "07 Armor and clothing";
            if (name.StartsWith("mod")) return "08 Item mods";
            if (name.StartsWith("vehicle") || name.StartsWith("resourcegas")) return "09 Vehicles and fuel";
            if (name.StartsWith("resource")) return "10 Resources";
            // Unknown items retain their own group; one arbitrary item must not
            // turn a chest into a catch-all destination.
            return "90 " + (groups != null && groups.Length != 0 ? groups[0] : name);
        }

        static bool InGroup(string[] groups, string group)
        {
            if (groups != null) foreach (string value in groups)
                if (string.Equals(value, group, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool MatchesDestination(bool exactItem, HashSet<string> seededCategories, string category)
        {
            return exactItem || (seededCategories != null && seededCategories.Contains(category));
        }

        public static bool RespawnDue(int touchedHours, int nowHours, int respawnDays)
        {
            return respawnDays > 0 && nowHours >= touchedHours &&
                ((long)nowHours - touchedHours) / 24 >= respawnDays;
        }

        public struct Drive
        {
            public float Forward, Steer;
            public bool Brake;
        }

        // Ground vehicle controller; steering and braking go through native
        // physics. Closing speed determines braking distance, not teleportation.
        public static Drive VehicleControl(float gap, float headingDegrees, float speed, float targetSpeed, bool blocked)
        {
            var result = new Drive { Steer = Math.Max(-1, Math.Min(1, headingDegrees / 35f)) };
            float brakingGap = 8f + Math.Max(0, speed * speed - targetSpeed * targetSpeed) / 6f;
            result.Brake = blocked || gap < brakingGap || (Math.Abs(headingDegrees) > 75 && speed > 2);
            result.Forward = result.Brake ? 0 : (Math.Abs(headingDegrees) > 60 ? 0.25f : Math.Min(1, Math.Max(0.15f, (gap - 8) / 18)));
            return result;
        }

        public static bool CancelFollow(bool manualInput, bool dead, bool targetDead, bool partyMember, float distance)
        {
            return manualInput || dead || targetDead || !partyMember || distance > 200;
        }
    }
}
