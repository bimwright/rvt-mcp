using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Failures
{
    /// <summary>One top-level window of the Revit process, as seen by one probe tick. No native handle leaves the probe.</summary>
    public sealed class WindowSighting
    {
        public long Handle { get; set; }
        public string ClassName { get; set; }
        public string Title { get; set; }
        public long Style { get; set; }
        public long ExStyle { get; set; }
        public bool SameUiThread { get; set; }
        public bool Visible { get; set; }
        public bool Enabled { get; set; }
        public bool IsOwn { get; set; }
        public bool HasProgressBar { get; set; }
    }

    /// <summary>One appearance of a window: a handle from the tick it is first seen until it is gone. A reused handle is a new appearance.</summary>
    public sealed class WindowAppearance
    {
        public string AppearanceId { get; } = Guid.NewGuid().ToString("N");
        public long Handle { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
        /// <summary>"window" waits for the user without a Revit event; "event" belongs to a DialogBoxShowing event; "progress" carries a progress bar.</summary>
        public string Kind { get; set; } = "window";
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSighting { get; set; } = DateTimeOffset.MinValue;
        public int Sightings { get; set; }
        public bool Confirmed { get; set; }
        /// <summary>Set by the integration: the MCP operation this appearance was reported to, if any.</summary>
        public string OperationId { get; set; }
        /// <summary>Set by the integration: context generation when first seen, for model attribution.</summary>
        public long ContextGeneration { get; set; }
        public object Content { get; set; }
        /// <summary>Set by the integration: its own record of this appearance for the operation.</summary>
        public object Record { get; set; }
    }

    /// <summary>
    /// The windows one MCP operation waited on. Each keeps the model of the context it was seen in only while that context is
    /// unchanged; otherwise it has no model and is never guessed onto another. Past the cap, windows are counted, not kept.
    /// </summary>
    public sealed class WindowOperationLog
    {
        public const int MaxWindows = 20;
        private sealed class Entry { internal Newtonsoft.Json.Linq.JObject Dialog; internal long Generation; internal string ModelScopeKey; internal bool Closed; }
        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        public int Omitted { get { lock (_gate) return _omitted; } }
        private int _omitted;

        /// <returns>A token for <see cref="Close"/>, or null when the window is past the cap (and only counted).</returns>
        public object Add(Newtonsoft.Json.Linq.JObject dialog, long firstSeenGeneration, string modelScopeKey)
        {
            lock (_gate)
            {
                if (_entries.Count >= MaxWindows) { _omitted++; return null; }
                var entry = new Entry { Dialog = (Newtonsoft.Json.Linq.JObject)dialog.DeepClone(), Generation = firstSeenGeneration, ModelScopeKey = modelScopeKey };
                _entries.Add(entry);
                return entry;
            }
        }

        public void Close(object token, bool contextUnchanged)
        {
            if (!(token is Entry entry)) return;
            lock (_gate) { if (!contextUnchanged) entry.ModelScopeKey = null; entry.Closed = true; }
        }

        /// <summary>Windows with a certain model, and windows without one. A window still open is certain only if nothing changed since.</summary>
        public void Finish(long currentGeneration, out List<KeyValuePair<Newtonsoft.Json.Linq.JObject, string>> withModel, out List<Newtonsoft.Json.Linq.JObject> withoutModel)
        {
            withModel = new List<KeyValuePair<Newtonsoft.Json.Linq.JObject, string>>();
            withoutModel = new List<Newtonsoft.Json.Linq.JObject>();
            lock (_gate)
                foreach (var entry in _entries)
                {
                    var key = entry.Closed || entry.Generation == currentGeneration ? entry.ModelScopeKey : null;
                    if (string.IsNullOrEmpty(key)) withoutModel.Add((Newtonsoft.Json.Linq.JObject)entry.Dialog.DeepClone());
                    else withModel.Add(new KeyValuePair<Newtonsoft.Json.Linq.JObject, string>((Newtonsoft.Json.Linq.JObject)entry.Dialog.DeepClone(), key));
                }
        }
    }

    public sealed class WindowModalTransitions
    {
        public List<WindowAppearance> Confirmed { get; } = new List<WindowAppearance>();
        public List<WindowAppearance> Closed { get; } = new List<WindowAppearance>();
        public List<WindowAppearance> New { get; } = new List<WindowAppearance>();
        /// <summary>Confirmed "window" appearances still open after this tick.</summary>
        public List<WindowAppearance> Open { get; } = new List<WindowAppearance>();
    }

    /// <summary>
    /// Rules of the issue-16 phase C window probe (spike findings 1-12, review of plan draft 1).
    /// Pure logic: the caller supplies the window list, the clock and whether a window's UI thread answers a no-op message.
    /// </summary>
    public sealed class WindowModalTracker
    {
        public const long WsPopup = 0x80000000L, WsCaption = 0x00C00000L, WsExToolWindow = 0x80L;
        public static readonly TimeSpan SightingSpacing = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan EventTicketLifetime = TimeSpan.FromSeconds(5);
        public const int SightingsToConfirm = 3;

        private sealed class EventTicket { internal DateTimeOffset Time; internal bool Answered, Used; }

        private readonly object _gate = new object();
        private readonly List<EventTicket> _tickets = new List<EventTicket>();
        private readonly Dictionary<long, WindowAppearance> _open = new Dictionary<long, WindowAppearance>();

        /// <summary>A Revit DialogBoxShowing event fired. Returns an action that marks it answered (the dialog then never shows).</summary>
        public Action NoteEvent(DateTimeOffset now)
        {
            var ticket = new EventTicket { Time = now };
            lock (_gate) { _tickets.Add(ticket); _tickets.RemoveAll(t => now - t.Time > EventTicketLifetime + EventTicketLifetime); }
            return () => { lock (_gate) ticket.Answered = true; };
        }

        public static bool IsCandidate(WindowSighting w)
        {
            if (w == null || !w.SameUiThread || !w.Visible || !w.Enabled || w.IsOwn) return false;
            bool dialogLike = (w.Style & WsPopup) != 0 || (w.Style & WsCaption) == WsCaption;
            return dialogLike && (w.ExStyle & WsExToolWindow) == 0;
        }

        public IReadOnlyCollection<WindowAppearance> OpenAppearances { get { lock (_gate) return _open.Values.ToList(); } }

        /// <param name="mainEnabled">Whether Revit's own main window is enabled. Only a disabled main window can be blocked by a modal.</param>
        /// <param name="windows">Top-level windows of the process (main window excluded).</param>
        /// <param name="pumps">Whether a candidate's UI thread answered a no-op message promptly; asked once per counted sighting.</param>
        public WindowModalTransitions Tick(DateTimeOffset now, bool mainEnabled, IEnumerable<WindowSighting> windows, Func<WindowSighting, bool> pumps)
        {
            var result = new WindowModalTransitions();
            lock (_gate)
            {
                var candidates = mainEnabled ? new List<WindowSighting>()
                    : (windows ?? Enumerable.Empty<WindowSighting>()).Where(IsCandidate).ToList();
                var seen = new HashSet<long>(candidates.Select(c => c.Handle));
                foreach (var handle in _open.Keys.Where(h => !seen.Contains(h)).ToList())
                {
                    result.Closed.Add(_open[handle]);
                    _open.Remove(handle);
                }
                foreach (var window in candidates)
                {
                    if (!_open.TryGetValue(window.Handle, out var appearance))
                    {
                        appearance = new WindowAppearance { Handle = window.Handle, Title = window.Title, ClassName = window.ClassName, FirstSeen = now };
                        // The first new window after an unused, unanswered event within the lifetime is that event's dialog.
                        var ticket = _tickets.Where(t => !t.Used && !t.Answered && now - t.Time <= EventTicketLifetime && t.Time <= now)
                            .OrderBy(t => t.Time).FirstOrDefault();
                        if (ticket != null) { ticket.Used = true; appearance.Kind = "event"; }
                        else if (window.HasProgressBar) appearance.Kind = "progress";
                        _open[window.Handle] = appearance;
                        result.New.Add(appearance);
                    }
                    // A progress bar may be added after the window first shows: check again until confirmed.
                    if (appearance.Kind == "window" && !appearance.Confirmed && window.HasProgressBar) appearance.Kind = "progress";
                    if (appearance.Kind != "window" || appearance.Confirmed) continue;
                    if (now - appearance.LastSighting < SightingSpacing) continue;
                    // A window whose UI thread does not pump is busy work (progress), not a dialog waiting for the user: start over.
                    if (pumps != null && !pumps(window)) { appearance.Sightings = 0; appearance.LastSighting = now; continue; }
                    appearance.Sightings++;
                    appearance.LastSighting = now;
                    if (appearance.Sightings >= SightingsToConfirm) { appearance.Confirmed = true; result.Confirmed.Add(appearance); }
                }
                result.Open.AddRange(_open.Values.Where(a => a.Kind == "window" && a.Confirmed));
                _tickets.RemoveAll(t => t.Used || now - t.Time > EventTicketLifetime);
            }
            return result;
        }

        /// <summary>A window's context is known only if no model opening, closing or view change happened since it was first seen.</summary>
        public static bool ContextUnchanged(long snapshotGeneration, long firstSeenGeneration, long currentGeneration)
            => snapshotGeneration == firstSeenGeneration && currentGeneration == firstSeenGeneration;

        /// <summary>Timeout for one message wait: never more than what is left of the whole read budget.</summary>
        public static uint RemainingTimeout(long elapsedMs, int budgetMs, int perMessageMs)
            => (uint)Math.Max(0, Math.Min(perMessageMs, budgetMs - elapsedMs));

        /// <summary>Forgets every open appearance (probe stopping); returns the confirmed ones so their state can be released.</summary>
        public List<WindowAppearance> Reset()
        {
            lock (_gate)
            {
                var confirmed = _open.Values.Where(a => a.Confirmed).ToList();
                _open.Clear(); _tickets.Clear();
                return confirmed;
            }
        }
    }
}
