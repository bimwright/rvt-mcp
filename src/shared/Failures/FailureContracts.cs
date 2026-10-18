using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace RvtMcp.Failures
{
    // API-free contracts: only captured facts belong here, never a live Revit object or raw request.
    public static class FailureJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            ContractResolver = new CamelCasePropertyNamesContractResolver(), NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTimeOffset };
        public static string Serialize(object value) { return JsonConvert.SerializeObject(value, Settings); }
        public static T Deserialize<T>(string value) { return JsonConvert.DeserializeObject<T>(value, Settings); }
    }
    public sealed class FailureModelIdentity
    {
        public string ModelKey { get; set; }
        public string PhysicalKey { get; set; }
        public string Title { get; set; }
        public string LiveDocumentId { get; set; }
        public string HostInstanceId { get; set; }
        public string KnownScopeKey { get; set; }
        public bool IsTransient { get; set; }
    }
    public sealed class FailureModel
    {
        public string ModelScopeKey { get; set; }
        public string ModelKey { get; set; }
        public string Title { get; set; }
        public bool IsTransient { get; set; }
        public long Revision { get; set; }
    }
    public sealed class FailureOperation
    {
        public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
        public string SessionId { get; set; }
        public string HostInstanceId { get; set; }
        public string Tool { get; set; }
        public string RequestHash { get; set; }
        public int RevitYear { get; set; }
        public string OperationContext { get; set; } = "edit";
        public string DocumentRevision { get; set; }
        public List<string> ModelScopeKeys { get; set; } = new List<string>();
        public string Execution { get; set; } = "running";
        public string Outcome { get; set; } = "unknown";
        public bool? MutationApplied { get; set; }
        public string ObservationCoverage { get; set; } = "supported_handlers_only";
        // Capture completeness is retained while the UI is waiting. Wire completion
        // additionally requires execution to have finished.
        [JsonIgnore]
        public bool Complete { get; set; } = true;
        [JsonProperty("complete")]
        private bool ReceiptComplete
        {
            get => Execution == "completed" && Complete;
            set => Complete = value;
        }
        public bool RequiresReview { get; set; }
        public string ActiveModelBefore { get; set; }
        public string ActiveModelAfter { get; set; }
        public List<string> IncidentIds { get; set; } = new List<string>();
        public JObject FinalResult { get; set; }
        public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }
    public sealed class FailureElementSnapshot
    {
        public long ElementId { get; set; }
        public string Category { get; set; }
        public string Type { get; set; }
        public string Level { get; set; }
        public double[] Position { get; set; }
        public bool? ExistsAfterRollback { get; set; }
    }
    public sealed class FailureMessage
    {
        public string DefinitionId { get; set; }
        public string Description { get; set; }
        public string Severity { get; set; } = "Unknown";
        public List<long> FailingElementIds { get; set; } = new List<long>();
        public List<long> AdditionalElementIds { get; set; } = new List<long>();
        public List<long> ExistingElementIds { get; set; } = new List<long>();
        public List<FailureElementSnapshot> Snapshots { get; set; } = new List<FailureElementSnapshot>();
        public string Source { get; set; }
        public string TransactionKey { get; set; }
        public string Cycle { get; set; }
        public int? Sequence { get; set; }
        public bool Complete { get; set; } = true;
        public bool TextTruncated { get; set; }
        public bool ResolutionsApplied { get; set; }
    }
    public sealed class FailureIncident
    {
        public string IncidentId { get; set; } = Guid.NewGuid().ToString("N");
        public string OperationId { get; set; }
        public string ModelScopeKey { get; set; }
        public string Source { get; set; }
        public string TransactionName { get; set; }
        public string RequestHash { get; set; }
        public string DocumentRevision { get; set; }
        public int RevitYear { get; set; }
        public string OperationContext { get; set; }
        public string NativeAction { get; set; }
        public string NativeContextHash { get; set; }
        public string NativeContextDescription { get; set; }
        public string ModelTitle { get; set; }
        public List<string> RelatedModelScopeKeys { get; set; } = new List<string>();
        public int? BatchIndex { get; set; }
        public string Severity { get; set; }
        public string Action { get; set; } = "rolled_back";
        public string State { get; set; } = "open";
        public bool Complete { get; set; } = true;
        public string CountKind { get; set; } = "exact";
        public int MessageCount { get; set; }
        public int OmittedMessageCount { get; set; }
        public int OmittedElementCount { get; set; }
        public List<FailureMessage> Messages { get; set; } = new List<FailureMessage>();
        public List<string> RuleIds { get; set; } = new List<string>();
        public Dictionary<string, long> RuleRevisions { get; set; } = new Dictionary<string, long>();
        public List<string> GrantIds { get; set; } = new List<string>();
        public string Persistence { get; set; } = "recorded";
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }
    public class FailureRule
    {
        public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
        public string ModelScopeKey { get; set; }
        public string DefinitionId { get; set; }
        public string Severity { get; set; } = "Warning";
        public int RevitYear { get; set; }
        public string OperationContext { get; set; } = "edit";
        public int MaxCount { get; set; }
        public List<long> ExistingElementIds { get; set; } = new List<long>();
        public long Revision { get; set; }
        public bool Enabled { get; set; } = true;
    }
    public sealed class FailureGrant : FailureRule
    {
        public string GrantId { get; set; } = Guid.NewGuid().ToString("N");
        public string RequestHash { get; set; }
        public string DocumentRevision { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public string ConsumedOperationId { get; set; }
        public string NativeAction { get; set; }
        public string NativeContextHash { get; set; }
    }
    public sealed class FailureDecision
    {
        public string DecisionId { get; set; } = Guid.NewGuid().ToString("N");
        public string IncidentId { get; set; }
        public string ModelScopeKey { get; set; }
        public string Action { get; set; }
        public string Reason { get; set; }
        public string WindowsUser { get; set; }
        public string RevitUser { get; set; }
        public string HostInstanceId { get; set; }
        public string Channel { get; set; } = "revit_ui";
        public bool VerifiedModel { get; set; }
        public string Verification { get; set; }
        public string DocumentRevision { get; set; }
        public FailureRule Rule { get; set; }
        public FailureGrant Grant { get; set; }
        public List<FailureRule> Rules { get; set; } = new List<FailureRule>();
        public List<FailureGrant> Grants { get; set; } = new List<FailureGrant>();
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }
    public sealed class FailurePersistenceResult
    {
        public string Persistence { get; set; }
        public bool Durable { get; set; }
        public string ErrorCode { get; set; }
        public bool Recorded { get { return Persistence == "recorded"; } }
    }
    public sealed class FailureGate
    {
        public bool Blocked { get; set; }
        public string Reason { get; set; }
        public long Revision { get; set; }
        public List<string> IncidentIds { get; set; } = new List<string>();
        public List<string> OperationIds { get; set; } = new List<string>();
    }
    public sealed class FailurePolicyResult
    {
        public string Action { get; set; }
        public string Reason { get; set; }
        public List<string> RuleIds { get; set; } = new List<string>();
        public List<string> GrantIds { get; set; } = new List<string>();
    }
    public sealed class FailureRecordQuery
    {
        public string ModelScopeKey { get; set; }
        public string IncidentId { get; set; }
        public string OperationId { get; set; }
        public string State { get; set; }
        public long? ElementId { get; set; }
        public DateTimeOffset? From { get; set; }
        public DateTimeOffset? Until { get; set; }
        public int Limit { get; set; } = 50;
        public string Cursor { get; set; }
        public bool IncludeDetails { get; set; }
        /// <summary>Highest seq first, so a bounded read keeps the most recent incidents.</summary>
        public bool NewestFirst { get; set; }
    }
    public sealed class FailureRecordPage
    {
        public bool Available { get; set; }
        public bool Complete { get; set; } = true;
        public List<FailureIncident> Records { get; set; } = new List<FailureIncident>();
        public string NextCursor { get; set; }
    }
}
