using Newtonsoft.Json.Linq;
using RvtMcp.Failures;

namespace RvtMcp.Server
{
    // All production access is under ToolGateway's connection lock. A declaration belongs
    // to one binding generation, not just a PID that Windows can reuse after restart.
    internal sealed class DialogAnswerSession
    {
        private string _target, _scope;
        private long _generation;
        private JArray _answers;
        private JObject _notice;

        internal long DocumentStamp { get; private set; }

        internal void Set(string target, long generation, string scope, JArray answers, long documentStamp)
        {
            _target = target; _generation = generation; _scope = scope;
            DocumentStamp = documentStamp;
            _answers = (JArray)answers.DeepClone();
            _notice = null;
        }

        // Explicit clear by the agent, or a reset: nothing to report.
        internal void Clear() { _answers = null; _target = null; _notice = null; }

        // The target or its active document changed. The agent did not clear the declaration,
        // so tell it once that later writes no longer carry it.
        internal void Drop()
        {
            if (_answers != null)
                _notice = new JObject
                {
                    ["status"] = "cleared",
                    ["reason"] = "binding_changed",
                    ["scope"] = _scope,
                    ["message"] = "Dialog answers declared earlier were cleared because the Revit target or its active document changed. "
                        + "Later writes do not carry them. Declare them again with revit_set_dialog_answers if still needed."
                };
            _answers = null; _target = null;
        }

        internal JArray ForWrite(string target, long generation, bool isWrite)
        {
            if (_answers != null && (_target != target || _generation != generation)) Drop();
            CheckDocument();
            return isWrite && _answers != null ? (JArray)_answers.DeepClone() : null;
        }

        // Called only after serialization and pre-send guards, immediately before the wire
        // attempt. An interrupted/partial write is unknown delivery and must not replay next.
        internal void Sending() { if (_scope == "next") { _answers = null; _target = null; } }

        private void CheckDocument()
        {
            if (_answers != null && DialogDocumentContext.Read(_target) != DocumentStamp) Drop();
        }

        internal JObject TakeNotice()
        {
            CheckDocument();
            var notice = _notice; _notice = null;
            return notice;
        }

        // A response with no room keeps the notice for the next one, unless the agent declared again meanwhile.
        internal void ReturnNotice(JObject notice) { if (_notice == null && _answers == null) _notice = notice; }
    }
}
