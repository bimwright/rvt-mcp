using System;
using System.IO;
using System.Security.Cryptography;

namespace RevitMcp.Plugin
{
    public static class AuthToken
    {
        private static string _token;

        public static string Current => _token;

        /// <summary>
        /// Revit version identifier (e.g. "R22", "R25"). Set by App.cs at startup.
        /// Used to create version-specific discovery files (portR22.txt, pipeR25.txt, etc.)
        /// </summary>
        public static string RevitVersion { get; set; }

        public static void GenerateAndPersist(int port)
        {
            _token = GenerateToken();
            var version = RevitVersion ?? "R22";
            var fileName = $"port{version}.txt";
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var content = port + "\n" + _token + "\n" + pid + "\n";
            WriteDiscoveryFile(fileName, content);
        }

        public static void GenerateAndPersistPipe(string pipeName)
        {
            _token = GenerateToken();
            var version = RevitVersion ?? "R27";
            var fileName = $"pipe{version}.txt";
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var content = pipeName + "\n" + _token + "\n" + pid + "\n";
            WriteDiscoveryFile(fileName, content);
        }

        /// <summary>
        /// Deletes the version-specific discovery file written by Generate* during startup.
        /// Called from transport Stop() so a clean Revit shutdown leaves no stale pipeR{ver}.txt
        /// or portR{ver}.txt behind (which would otherwise cause the MCP server to attempt
        /// connections to a dead plugin).
        /// </summary>
        /// <param name="kind">"port" or "pipe" — matches the filename prefix.</param>
        public static void DeleteDiscoveryFile(string kind)
        {
            if (string.IsNullOrEmpty(RevitVersion)) return;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevitMcp");
                var filePath = Path.Combine(dir, $"{kind}{RevitVersion}.txt");
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

        private static void WriteDiscoveryFile(string fileName, string content)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMcp");
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
                System.Diagnostics.Debug.WriteLine($"[RevitMCP] ACL restriction failed for {fileName}: {ex.Message}");
            }
        }
    }
}
