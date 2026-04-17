using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Bimwright.Plugin.ToolBaker
{
    public class BakedToolMeta
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string ParametersSchema { get; set; }
        public string CreatedUtc { get; set; }
        public int CallCount { get; set; }
    }

    public class BakedToolRegistry
    {
        private readonly string _dir;
        private readonly string _registryPath;
        private readonly Dictionary<string, BakedToolMeta> _tools = new Dictionary<string, BakedToolMeta>();

        public BakedToolRegistry()
        {
            _dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "baked");
            Directory.CreateDirectory(_dir);
            _registryPath = Path.Combine(_dir, "registry.json");
            Load();
        }

        public string BakedDir => _dir;

        public void Save(BakedToolMeta meta, string sourceCode)
        {
            _tools[meta.Name] = meta;
            // Save source
            File.WriteAllText(Path.Combine(_dir, meta.Name + ".cs"), sourceCode);
            // Save registry
            var json = JsonConvert.SerializeObject(_tools.Values, Formatting.Indented);
            File.WriteAllText(_registryPath, json);
        }

        public string GetSource(string name)
        {
            var path = Path.Combine(_dir, name + ".cs");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public BakedToolMeta GetMeta(string name)
        {
            _tools.TryGetValue(name, out var meta);
            return meta;
        }

        public IEnumerable<BakedToolMeta> GetAll() => _tools.Values;

        public void IncrementCallCount(string name)
        {
            if (_tools.TryGetValue(name, out var meta))
            {
                meta.CallCount++;
                var json = JsonConvert.SerializeObject(_tools.Values, Formatting.Indented);
                File.WriteAllText(_registryPath, json);
            }
        }

        public bool Remove(string name)
        {
            if (!_tools.Remove(name)) return false;
            var csPath = Path.Combine(_dir, name + ".cs");
            if (File.Exists(csPath)) File.Delete(csPath);
            var json = JsonConvert.SerializeObject(_tools.Values, Formatting.Indented);
            File.WriteAllText(_registryPath, json);
            return true;
        }

        private void Load()
        {
            if (!File.Exists(_registryPath)) return;
            try
            {
                var json = File.ReadAllText(_registryPath);
                var list = JsonConvert.DeserializeObject<List<BakedToolMeta>>(json);
                if (list == null) return;
                foreach (var meta in list)
                    _tools[meta.Name] = meta;
            }
            catch { }
        }
    }
}
