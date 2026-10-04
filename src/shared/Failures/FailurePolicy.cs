using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Failures
{
    public sealed class FailureDialogAdapter
    {
        public string DialogId { get; internal set; }
        public string EventType { get; internal set; } = "TaskDialog";
        public string OperationContext { get; internal set; }
        public string ButtonSemantic { get; internal set; }
        public int Result { get; internal set; }
    }
    public static class FailureDialogAdapters
    {
        // Native observations verified only for these two years. Other years remain manual until accepted.
        public static FailureDialogAdapter Get(string dialogId, int revitYear, string operationContext)
        {
            if (revitYear != 2022 && revitYear != 2027) return null;
            if (dialogId == "TaskDialog_Unresolved_References" && operationContext == "open:ignore_unresolved_references")
                return new FailureDialogAdapter { DialogId = dialogId, OperationContext = operationContext, ButtonSemantic = "Ignore unresolved references for this open", Result = 1002 };
            if (dialogId == "TaskDialog_Views_Related_To_Analytical_Changed" && operationContext == "open:acknowledge_analytical_changes")
                return new FailureDialogAdapter { DialogId = dialogId, OperationContext = operationContext, ButtonSemantic = "Acknowledge analytical view changes", Result = 2 };
            return null;
        }
    }
    public static class FailurePolicy
    {
        // Pure policy. The adapter still has to persist the returned acceptance before deleting warnings.
        public static FailurePolicyResult Evaluate(IEnumerable<FailureMessage> messages, bool complete,
            string modelScopeKey, int revitYear, string operationContext, string requestHash,
            string documentRevision, IEnumerable<FailureRule> rules = null,
            IEnumerable<FailureGrant> grants = null, DateTimeOffset? now = null)
        {
            var all = (messages ?? Enumerable.Empty<FailureMessage>()).ToList();
            if (!complete || all.Any(m => m == null || !m.Complete)) return Stop("FAILURE_CAPTURE_INCOMPLETE");
            if (all.Count == 0) return new FailurePolicyResult { Action = "continue" };
            if (all.Any(m => m.Severity != "Warning" || string.IsNullOrEmpty(m.DefinitionId)))
                return Stop("REVIT_FAILURE_ROLLED_BACK");
            var result = new FailurePolicyResult { Action = "continue", Reason = "accepted_warnings" };
            foreach (var group in all.GroupBy(m => m.DefinitionId, StringComparer.OrdinalIgnoreCase))
            {
                var elements = new HashSet<long>(group.SelectMany(m => m.ExistingElementIds));
                Func<FailureRule, bool> matches = r => r != null && r.Enabled && r.ModelScopeKey == modelScopeKey
                    && r.Severity == "Warning" && string.Equals(r.DefinitionId, group.Key, StringComparison.OrdinalIgnoreCase)
                    && r.RevitYear == revitYear && r.OperationContext == operationContext
                    && r.MaxCount >= group.Count() && elements.IsSubsetOf(r.ExistingElementIds ?? new List<long>());
                var rule = (rules ?? Enumerable.Empty<FailureRule>()).FirstOrDefault(matches);
                if (rule != null) { result.RuleIds.Add(rule.RuleId); continue; }
                var grant = (grants ?? Enumerable.Empty<FailureGrant>()).FirstOrDefault(g => matches(g)
                    && string.IsNullOrEmpty(g.NativeAction) && !string.IsNullOrEmpty(requestHash)
                    && g.RequestHash == requestHash && !string.IsNullOrEmpty(documentRevision)
                    && g.DocumentRevision == documentRevision && string.IsNullOrEmpty(g.ConsumedOperationId)
                    && g.ExpiresUtc > (now ?? DateTimeOffset.UtcNow));
                if (grant == null) return Stop("REVIT_FAILURE_ROLLED_BACK");
                result.GrantIds.Add(grant.GrantId);
            }
            return result;
        }

        public static FailureGrant MatchNativeGrant(IEnumerable<FailureGrant> grants, string scope, int year,
            string action, string contextHash, string requestHash, string revision, DateTimeOffset? now = null)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(action) || string.IsNullOrEmpty(contextHash)
                || string.IsNullOrEmpty(requestHash) || string.IsNullOrEmpty(revision)) return null;
            return (grants ?? Enumerable.Empty<FailureGrant>()).FirstOrDefault(g => g.Enabled
                && g.ModelScopeKey == scope && g.RevitYear == year && g.NativeAction == action
                && g.NativeContextHash == contextHash && g.RequestHash == requestHash
                && g.DocumentRevision == revision && string.IsNullOrEmpty(g.ConsumedOperationId)
                && g.ExpiresUtc > (now ?? DateTimeOffset.UtcNow));
        }
        public static FailurePolicyResult EvaluateDialog(string dialogId, int revitYear, string operationContext,
            string modelScopeKey, string requestHash, string documentRevision, IEnumerable<FailureRule> rules = null,
            IEnumerable<FailureGrant> grants = null, DateTimeOffset? now = null)
        {
            if (FailureDialogAdapters.Get(dialogId, revitYear, operationContext) == null) return Stop("REVIT_OPERATION_REQUIRES_USER");
            Func<FailureRule, bool> matches = r => r != null && r.Enabled && r.ModelScopeKey == modelScopeKey && r.Severity == "TaskDialog"
                && r.DefinitionId == dialogId && r.RevitYear == revitYear && r.OperationContext == operationContext
                && r.MaxCount == 1 && r.ExistingElementIds != null && r.ExistingElementIds.Count == 0;
            var rule = (rules ?? Enumerable.Empty<FailureRule>()).FirstOrDefault(matches);
            if (rule != null) return new FailurePolicyResult { Action = "continue", Reason = "accepted_dialog", RuleIds = new List<string> { rule.RuleId } };
            var grant = (grants ?? Enumerable.Empty<FailureGrant>()).FirstOrDefault(g => matches(g) && string.IsNullOrEmpty(g.NativeAction)
                && !string.IsNullOrEmpty(requestHash) && g.RequestHash == requestHash && !string.IsNullOrEmpty(documentRevision)
                && g.DocumentRevision == documentRevision && string.IsNullOrEmpty(g.ConsumedOperationId)
                && g.ExpiresUtc > (now ?? DateTimeOffset.UtcNow));
            return grant == null ? Stop("REVIT_OPERATION_REQUIRES_USER")
                : new FailurePolicyResult { Action = "continue", Reason = "accepted_dialog", GrantIds = new List<string> { grant.GrantId } };
        }
        private static FailurePolicyResult Stop(string reason) { return new FailurePolicyResult { Action = "rollback", Reason = reason }; }
    }
}
