using System.Collections.Generic;
using System.Linq;

namespace Bimwright.Rvt.Server.Memory
{
    public class PatternInfo
    {
        public string Tool { get; set; }
        public int CallCount { get; set; }
        public int ErrorCount { get; set; }
        public double ErrorRate => CallCount > 0 ? (double)ErrorCount / CallCount : 0;
    }

    public class PatternReport
    {
        public int TotalCalls { get; set; }
        public int TotalErrors { get; set; }
        public PatternInfo[] TopTools { get; set; }
        public PatternInfo[] ErrorProne { get; set; }
        public string[] Flags { get; set; }
    }

    public class PatternDetector
    {
        private readonly Dictionary<string, PatternInfo> _tools = new Dictionary<string, PatternInfo>();
        private readonly List<string> _recentTools = new List<string>();
        private readonly object _lock = new object();
        private const int RecentBufferSize = 100;
        private const int RepeatThreshold = 5;
        private const double ErrorRateThreshold = 0.5;

        public void Record(string toolName, bool success)
        {
            lock (_lock)
            {
                if (!_tools.TryGetValue(toolName, out var info))
                {
                    info = new PatternInfo { Tool = toolName };
                    _tools[toolName] = info;
                }
                info.CallCount++;
                if (!success) info.ErrorCount++;

                _recentTools.Add(toolName);
                if (_recentTools.Count > RecentBufferSize)
                    _recentTools.RemoveRange(0, _recentTools.Count - RecentBufferSize);
            }
        }

        public PatternReport GetReport()
        {
            lock (_lock)
            {
            var allInfos = _tools.Values.ToList();
            var totalCalls = allInfos.Sum(i => i.CallCount);
            var totalErrors = allInfos.Sum(i => i.ErrorCount);

            var flags = new List<string>();

            if (_recentTools.Count >= RepeatThreshold)
            {
                var last = _recentTools.Skip(_recentTools.Count - RepeatThreshold).ToList();
                if (last.Distinct().Count() == 1)
                    flags.Add($"Repeated: {last[0]} called {RepeatThreshold}+ times consecutively");
            }

            var errorProne = allInfos
                .Where(i => i.CallCount >= 3 && i.ErrorRate >= ErrorRateThreshold)
                .OrderByDescending(i => i.ErrorRate)
                .ToArray();

            foreach (var ep in errorProne)
                flags.Add($"High error rate: {ep.Tool} ({ep.ErrorCount}/{ep.CallCount} = {ep.ErrorRate:P0})");

            return new PatternReport
            {
                TotalCalls = totalCalls,
                TotalErrors = totalErrors,
                TopTools = allInfos.OrderByDescending(i => i.CallCount).Take(10).ToArray(),
                ErrorProne = errorProne,
                Flags = flags.ToArray()
            };
            }
        }
    }
}
