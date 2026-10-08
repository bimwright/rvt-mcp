using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace RvtMcp.Plugin
{
    public static class AuthToken
    {
        private static string _token;
        private static readonly int CachedPid = CurrentPid();
        public static string Current => _token;
        public static string RevitVersion { get; set; }
        public static string TargetId => "revit-" + (RevitVersion ?? "2022") + "-" + CachedPid;

        private static int CurrentPid()
        {
            using (var process = Process.GetCurrentProcess()) { return process.Id; }
        }

        public static DiscoveryPublisher GenerateAndPersist(int port, string directory = null) =>
            Generate("tcp", port, null, directory);

        public static DiscoveryPublisher GenerateAndPersistPipe(string pipeName, string directory = null) =>
            Generate("pipe", null, pipeName, directory);

        private static DiscoveryPublisher Generate(string transport, int? port, string pipeName, string directory)
        {
            _token = GenerateToken();
            using (var process = Process.GetCurrentProcess())
            {
                var publisher = new DiscoveryPublisher(directory ?? DiscoveryDir(), RevitVersion ?? "2022",
                    process.Id, process.StartTime.ToUniversalTime(), restrictAcl: RestrictAcl,
                    log: message => Debug.WriteLine("[RvtMcp] " + message));
                publisher.Publish(transport, port, pipeName, _token);
                return publisher;
            }
        }

        public static bool Verify(string candidate)
        {
            var token = _token; // Listener restart may replace the token concurrently.
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(candidate) || candidate.Length != token.Length) return false;
            int diff = 0;
            for (int i = 0; i < token.Length; i++) diff |= token[i] ^ candidate[i];
            return diff == 0;
        }

        public static string DiscoveryDir() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");

        public static string DiscoveryFileName(string year) => "revit-" + year + "-" + CachedPid + ".json";

        private static string GenerateToken()
        {
            var bytes = new byte[32];
#if NET5_0_OR_GREATER
            RandomNumberGenerator.Fill(bytes);
#else
            using (var rng = new RNGCryptoServiceProvider()) { rng.GetBytes(bytes); }
#endif
            return Convert.ToBase64String(bytes);
        }

        private static void RestrictAcl(string path)
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.SetAccessRuleProtection(true, false);
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            file.SetAccessControl(acl);
        }
    }
}
