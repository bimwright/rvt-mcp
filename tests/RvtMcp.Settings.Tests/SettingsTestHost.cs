using System;
using RvtMcp.Plugin;

// Only native host/toast/transport boundaries for the real SettingsViewModel.
namespace RvtMcp.Plugin
{
    public sealed class App
    {
        public RvtMcpConfig Config = new RvtMcpConfig();
        public bool ToastEnabled = true;
        public bool IsTransportRunning => false;
        public TestTransport Transport => null;
        public TestToastNotifier ToastNotifier => null;
        public event EventHandler ToastEnabledChanged { add { } remove { } }
        public void StopTransport() => throw new NotSupportedException("No native transport in Settings tests.");
        public void CreateAndStartTransport() => throw new NotSupportedException("No native transport in Settings tests.");
        public static void DebugLog(string message) { }
    }
    public class TestTransport { public string ConnectionInfo => ""; public bool IsClientConnected => false; }
    public sealed class TcpTransportServer : TestTransport { public int Port => 0; }
    public sealed class TestToastNotifier
    {
        public bool ShowBranding => false;
        public ToastPositionOptions PositionOptions => new ToastPositionOptions();
        public event Action<bool> PositionSaved { add { } remove { } }
        public void OnToastEnabledChanged(bool enabled, bool persisted) { }
        public void SetShowBranding(bool show) { }
        public void SetPositionOptions(ToastPositionOptions position) { }
    }
    public static class AuthToken { public static string RevitVersion => "2027"; }
}
