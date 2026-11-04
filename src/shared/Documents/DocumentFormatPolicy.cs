using System.Globalization;

namespace RvtMcp.Plugin.Documents
{
    public static class DocumentFormatPolicy
    {
        public static bool? RequiresUpgrade(string savedFormat, int hostYear)
        {
            if (!int.TryParse(savedFormat, NumberStyles.None, CultureInfo.InvariantCulture, out var savedYear)
                || savedYear < 2000 || savedYear > 2100 || hostYear < 2000 || hostYear > 2100)
                return null;
            return savedYear < hostYear;
        }
    }
}
