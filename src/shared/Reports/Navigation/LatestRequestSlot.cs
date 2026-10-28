using System.Threading;

namespace RvtMcp.Plugin.Reports.Navigation
{
    /// <summary>Holds only the newest request: a click made while another is still waiting replaces it.</summary>
    public sealed class LatestRequestSlot<T> where T : class
    {
        private T _value;

        public void Put(T value) => Interlocked.Exchange(ref _value, value);

        /// <summary>The waiting request, or null; the slot is empty afterwards.</summary>
        public T Take() => Interlocked.Exchange(ref _value, null);
    }
}
