namespace JonLootSkulls { public static class Rules {
        public static bool RespawnDue(int touchedHours, int nowHours, int respawnDays)
        {
            return respawnDays > 0 && nowHours >= touchedHours &&
                ((long)nowHours - touchedHours) / 24 >= respawnDays;
        }
} }
