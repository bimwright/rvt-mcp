using System;
using System.Threading.Tasks;

namespace Bimwright.Plugin
{
    public interface ITransportServer : IDisposable
    {
        void Start(Action<string, TaskCompletionSource<string>> onRequest);
        void Stop();
        string ConnectionInfo { get; }
        bool IsClientConnected { get; }
        DateTime? LastCommandTime { get; }
    }
}
