using System;
using System.Threading.Tasks;

namespace RvtMcp.Plugin
{
    public interface ITransportServer : IDisposable
    {
        void Start(Action<string, TaskCompletionSource<string>> onRequest);
        void Stop();
        bool IsRunning { get; }
        string ConnectionInfo { get; }
        bool IsClientConnected { get; }
        DateTime? LastCommandTime { get; }
    }
}
