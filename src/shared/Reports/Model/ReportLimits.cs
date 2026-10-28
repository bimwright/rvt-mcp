using System.Text;

namespace RvtMcp.Plugin.Reports.Model
{
    public static class ReportLimits
    {
        public const int TaskIdLength = 80;
        public const int SummaryLength = 120;
        public const int BodyBytes = 32 * 1024;
        public const int Links = 300;
        public const int IdsPerLink = 500;
        public const int TotalIds = 2000;

        public static string Validate(string taskId, string outcome, string summary, string body)
        {
            if (taskId == null || taskId.Length < 1 || taskId.Length > TaskIdLength)
                return "task_id must contain 1-80 characters.";
            if (outcome != "completed" && outcome != "failed" && outcome != "cancelled")
                return "outcome must be completed, failed or cancelled.";
            if (summary == null || summary.Length < 1 || summary.Length > SummaryLength)
                return "summary must contain 1-120 characters.";
            if (summary.IndexOfAny(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' }) >= 0)
                return "summary must be one line.";
            if (body != null && Encoding.UTF8.GetByteCount(body) > BodyBytes)
                return "body must not exceed 32768 UTF-8 bytes.";
            return null;
        }
    }
}
