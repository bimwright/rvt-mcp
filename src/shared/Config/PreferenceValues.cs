namespace RvtMcp.Plugin
{
    /// <summary>Pure preference validation, shared by configuration and presentation state.</summary>
    public static class PreferenceValues
    {
        public const int DefaultToastIdleSeconds = 20;
        public const int DefaultJournalHours = 4;

        public static int ToastIdleSeconds(int? seconds)
        {
            switch (seconds)
            {
                case 10: case 20: case 30: case 60: return seconds.Value;
                default: return DefaultToastIdleSeconds;
            }
        }

        public static int JournalHours(int? hours) =>
            hours >= 1 && hours <= 14 * 24 ? hours.Value : DefaultJournalHours;
    }
}
