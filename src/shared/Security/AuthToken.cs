using System;
using System.IO;
using System.Security.Cryptography;

namespace RvtMcp.Plugin
{
    public static class AuthToken
    {
        private static string _token;

        public static string Current => _token;

        /// <summary>
        /// Calendar-year Revit version (e.g. "2022", "2025"). Set by each plugin's App.cs
        /// in OnStartup before the transport starts. Used to name the discovery file
        /// (revit-YYYY.json) that the MCP server scans on connect.
        /// </summary>
        public static string RevitVersion { get; set; }

        public static void GenerateAndPersist(int port)
        {
            _token = GenerateToken();
            var year = RevitVersion ?? "2022";
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var json = BuildDiscoveryJson(year, transport: "tcp", port: port, pipeName: null, authToken: _token, pid: pid);
            WriteDiscoveryFile(DiscoveryFileName(year), json);
        }

        public static void GenerateAndPersistPipe(string pipeName)
        {
            _token = GenerateToken();
            var year = RevitVersion ?? "2027";
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var json = BuildDiscoveryJson(year, transport: "pipe", port: null, pipeName: pipeName, authToken: _token, pid: pid);
            WriteDiscoveryFile(DiscoveryFileName(year), json);
        }

        /// <summary>
        /// Deletes this plugin's discovery file on a clean shutdown so the MCP server
        /// doesn't waste a connect attempt on a dead plugin.
        /// </summary>
        public static void DeleteDiscoveryFile()
        {
            if (string.IsNullOrEmpty(RevitVersion)) return;
            try
            {
                var dir = DiscoveryDir();
                var filePath = Path.Combine(dir, DiscoveryFileName(RevitVersion));
                if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch { /* best-effort on shutdown path */ }
        }

        public static bool Verify(string candidate)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(candidate))
                return false;
            if (candidate.Length != _token.Length) return false;
            // constant-time compare
            int diff = 0;
            for (int i = 0; i < _token.Length; i++)
                diff |= _token[i] ^ candidate[i];
            return diff == 0;
        }

        public static string DiscoveryDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RvtMcp");
        }

        public static string DiscoveryFileName(string year)
        {
            return "revit-" + year + ".json";
        }

        private static string GenerateToken()
        {
            var bytes = new byte[32];
#if NET5_0_OR_GREATER
            RandomNumberGenerator.Fill(bytes);
#else
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }
#endif
            return Convert.ToBase64String(bytes);
        }

        private static string BuildDiscoveryJson(string year, string transport, int? port, string pipeName, string authToken, int pid)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema_version\": 2,\n");
            sb.Append("  \"revit_year\": ").Append(year).Append(",\n");
            sb.Append("  \"transport\": \"").Append(transport).Append("\",\n");
            if (port.HasValue)
                sb.Append("  \"port\": ").Append(port.Value).Append(",\n");
            else
                sb.Append("  \"port\": null,\n");
            if (pipeName != null)
                sb.Append("  \"pipe_name\": \"").Append(JsonEscape(pipeName)).Append("\",\n");
            else
                sb.Append("  \"pipe_name\": null,\n");
            sb.Append("  \"auth_token\": \"").Append(JsonEscape(authToken)).Append("\",\n");
            sb.Append("  \"pid\": ").Append(pid).Append(",\n");
            sb.Append("  \"capabilities\": [\"tool_catalog\"]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void WriteDiscoveryFile(string fileName, string content)
        {
            var dir = DiscoveryDir();
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, fileName);

            // Atomic write: temp file + replace
            var tmp = filePath + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(filePath)) File.Delete(filePath);
            File.Move(tmp, filePath);

            // Best-effort restrict ACL to current user
            try
            {
                var fi = new FileInfo(filePath);
                var acl = fi.GetAccessControl();
                acl.SetAccessRuleProtection(true, false);
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                fi.SetAccessControl(acl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RvtMcp] ACL restriction failed for {fileName}: {ex.Message}");
            }
        }
    }
}
