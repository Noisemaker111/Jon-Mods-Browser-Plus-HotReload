using System;
using System.Collections.Generic;

namespace JonFollow
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
            // Attachments inherit weapon/armor tags. Their own group wins.
            if (InGroup(groups, "Mods") || tag("itemModifier") || tag("modification") || name.StartsWith("mod")) return "08 Item mods";
            // Planting seeds are block items in the native game.
            if (name.StartsWith("planted") || tag("seed") || name.StartsWith("seed")) return "11 Farming";
            if (block) return "12 Building";
            if (InGroup(groups,"Medical") || tag("medical") || tag("medicine") || name.StartsWith("medical") || name.StartsWith("drug")) return "02 Medicine";
            if (InGroup(groups,"Ammo") || InGroup(groups,"Ammunition") || tag("ammo") || tag("ammunition") || name.StartsWith("ammo")) return "04 Ammunition";
            if (InGroup(groups,"Tools/Traps") || InGroup(groups,"Tools") || tag("tool") || tag("tools")) return "05 Tools";
            if (InGroup(groups,"Food/Cooking") || tag("foods") || tag("drinks") || tag("food") || tag("drink") || name.StartsWith("food") || name.StartsWith("drink")) return "03 Food and drink";
            if (InGroup(groups,"Ranged Weapons") || InGroup(groups,"Melee Weapons") || tag("weapon") || name.StartsWith("gun") || name.StartsWith("melee")) return "06 Weapons";
            if (InGroup(groups,"Armor") || InGroup(groups,"Clothing") || tag("armor") || tag("clothing") || name.StartsWith("armor") || name.StartsWith("apparel")) return "07 Armor and clothing";
            if (InGroup(groups,"Vehicles") || name.StartsWith("vehicle") || name.StartsWith("resourcegas")) return "09 Vehicles and fuel";
            if (InGroup(groups,"Resources") || name.StartsWith("resource")) return "10 Resources";
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

        // Follow is a short tether: it starts close and ends when the leader
        // pulls away (a bike cannot hang on to a car), the follower acts,
        // dies or leaves the party. A downed friend nearby is waited for.
        public const float StartRange = 20f, TetherRange = 45f;
        public static bool CancelFollow(bool manualInput, bool dead, bool partyMember, float gap)
        {
            return manualInput || dead || !partyMember || gap > TetherRange;
        }

        // Matches the leader's pace with hysteresis: sprint when they sprint
        // or the gap opens, walk again once close behind a walking leader.
        public static bool Sprint(bool sprinting, float leaderSpeed, float gap)
        {
            if (leaderSpeed > 5f || gap > 7f) return true;
            if (leaderSpeed < 4f && gap < 4.5f) return false;
            return sprinting;
        }
    }
}
