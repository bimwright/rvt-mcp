using System;
using System.Diagnostics;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>What the single toast window shows. Immutable; a new one per change.</summary>
    public sealed class ActivitySnapshot
    {
        public ActivitySnapshot(long cardId, bool isStatus, int succeeded, int failed, int images,
            string title, string body, bool latestSuccess, bool hasFailure,
            Func<ActivityStatusText> statusTextProvider = null)
        {
            CardId = cardId;
            IsStatus = isStatus;
            Succeeded = succeeded;
            Failed = failed;
            Images = images;
            Title = title;
            Body = body;
            LatestSuccess = latestSuccess;
            HasFailure = hasFailure;
            StatusTextProvider = statusTextProvider;
        }

        /// <summary>Never reused — a window callback carrying an old id is ignored.</summary>
        public long CardId { get; }
        /// <summary>Connected / toast on / toast off confirmation: no counters.</summary>
        public bool IsStatus { get; }
        public int Succeeded { get; }
        public int Failed { get; }
        public int Images { get; }
        /// <summary>Activity card: the latest tool. Status card: the status title.</summary>
        public string Title { get; }
        /// <summary>Activity card: the latest result's body. Status card: the status line.</summary>
        public string Body { get; }
        public bool LatestSuccess { get; }
        /// <summary>Set by the card's first failure and kept until the card closes.</summary>
        public bool HasFailure { get; }
        /// <summary>
        /// Optional late-bound status copy. The toast can re-resolve localized status
        /// text after L.Changed without creating a new card or touching its deadline.
        /// Activity cards leave this null and keep their captured result text.
        /// </summary>
        public Func<ActivityStatusText> StatusTextProvider { get; }
    }

    /// <summary>Localized title/body for a status card, resolved at render time.</summary>
    public sealed class ActivityStatusText
    {
        public ActivityStatusText(string title, string body)
        {
            Title = title;
            Body = body;
        }

        public string Title { get; }
        public string Body { get; }
    }

    public enum ActivityCardPhase { Hidden, Visible, Closing }

    public readonly struct ActivityRender
    {
        public ActivityRender(ActivityCardPhase phase, ActivitySnapshot card)
        {
            Phase = phase;
            Card = card;
        }

        public ActivityCardPhase Phase { get; }
        /// <summary>The card to show, or to fade out when Closing; null when Hidden.</summary>
        public ActivitySnapshot Card { get; }
    }

    /// <summary>
    /// State of the one activity toast: per-card counters, the idle deadline, hover pause,
    /// minimize/modal parking and status cards. Pure and clock-injected so the timing rules
    /// are unit-tested; the WPF side only reconciles its single window to <see cref="TakeRender"/>.
    ///
    /// Mutators return true when the caller must post a render. Further changes before that
    /// render runs return false, so a burst of results costs one render. Only a new result and
    /// a real pointer leave re-arm the deadline — rendering, ticking and status cards never do.
    /// </summary>
    public sealed class ActivityAggregator
    {
        public const int DefaultIdleSeconds = 20;

        // Pending: counted while the Revit frame was unusable, shown by FlushIfUsable.
        private enum Phase { None, Pending, Visible, Closing }

        private readonly object _gate = new object();
        private readonly Func<int> _idleSeconds;
        private readonly Func<TimeSpan> _now;

        private Phase _phase;
        private long _cardId;
        private long _lastCardId;
        private bool _isStatus;
        private int _statusSeconds;
        private int _succeeded;
        private int _failed;
        private int _images;
        private string _title;
        private string _body;
        private bool _latestSuccess;
        private bool _hasFailure;
        private Func<ActivityStatusText> _statusTextProvider;
        private bool _hovering;
        private TimeSpan _deadline;
        private bool _renderPending;

        /// <param name="idleSeconds">Read each time the deadline is set, so a changed setting
        /// applies from the next re-arm. Called under the lock — must be cheap.</param>
        /// <param name="now">Monotonic clock; defaults to a Stopwatch.</param>
        public ActivityAggregator(Func<int> idleSeconds = null, Func<TimeSpan> now = null)
        {
            _idleSeconds = idleSeconds ?? (() => DefaultIdleSeconds);
            if (now == null)
            {
                var sw = Stopwatch.StartNew();
                now = () => sw.Elapsed;
            }
            _now = now;
        }

        public bool HasUnrenderedResults
        {
            get { lock (_gate) return _phase == Phase.Pending; }
        }

        public bool RecordResult(string title, string body, bool success, bool hasImage, bool frameUsable)
        {
            lock (_gate)
            {
                var activityOpen = !_isStatus && (_phase == Phase.Visible || _phase == Phase.Pending);
                if (!activityOpen)
                    StartCard(isStatus: false, frameUsable ? Phase.Visible : Phase.Pending);
                else if (_phase == Phase.Pending && frameUsable)
                    _phase = Phase.Visible;

                if (success)
                {
                    _succeeded++;
                    if (hasImage)
                        _images++;
                }
                else
                {
                    _failed++;
                    _hasFailure = true;
                }
                _title = title;
                _body = body;
                _latestSuccess = success;

                // A result can arrive after the owner became minimized/disabled but
                // before the manager's next timer tick. Park the visible card now so
                // the queued render cannot update or expose a window over a modal frame.
                if (!_isStatus && !frameUsable && _phase == Phase.Visible)
                    _phase = Phase.Pending;

                if (_phase == Phase.Visible)
                    Rearm();
                return RequestRender();
            }
        }

        /// <summary>A status card never covers an activity card that is open.</summary>
        public bool ShowStatus(string title, string body, int seconds,
            Func<ActivityStatusText> statusTextProvider = null)
        {
            lock (_gate)
            {
                if (!_isStatus && (_phase == Phase.Visible || _phase == Phase.Pending))
                    return false;

                StartCard(isStatus: true, Phase.Visible);
                _statusSeconds = seconds > 0 ? seconds : DefaultIdleSeconds;
                _title = title;
                _body = body;
                _statusTextProvider = statusTextProvider;
                _latestSuccess = true;
                Rearm();
                return RequestRender();
            }
        }

        /// <summary>Toast turned on or off: drop the card and its counters.</summary>
        public bool Reset()
        {
            lock (_gate)
            {
                if (_phase == Phase.None)
                    return false;
                _phase = Phase.None;
                return RequestRender();
            }
        }

        /// <summary>Idling: show results counted while the frame was unusable, with a fresh deadline.</summary>
        public bool FlushIfUsable(bool frameUsable)
        {
            lock (_gate)
            {
                if (_phase != Phase.Pending || !frameUsable)
                    return false;
                _phase = Phase.Visible;
                _hovering = false;
                Rearm();
                return RequestRender();
            }
        }

        /// <summary>
        /// Timer: close an expired card. An activity card on an unusable frame is parked
        /// instead, so a minimized Revit comes back to the full counts, not to nothing.
        /// </summary>
        public bool Tick(bool frameUsable)
        {
            lock (_gate)
            {
                if (_phase != Phase.Visible)
                    return false;
                if (!_isStatus && !frameUsable)
                {
                    _phase = Phase.Pending;
                    _hovering = false;
                    return RequestRender();
                }
                if (!Expired())
                    return false;
                _phase = Phase.Closing;
                return RequestRender();
            }
        }

        public void PointerEntered(long cardId)
        {
            lock (_gate)
            {
                if (!IsLive(cardId))
                    return;

                // A late MouseEnter can arrive after the deadline but before the
                // timer tick. Do not pause an already expired card indefinitely.
                if (Expired())
                {
                    _phase = Phase.Closing;
                    RequestRender();
                    return;
                }

                _hovering = true;
            }
        }

        public void PointerLeft(long cardId)
        {
            lock (_gate)
            {
                if (!IsLive(cardId) || !_hovering)
                    return;
                _hovering = false;
                Rearm();
            }
        }

        /// <summary>× or a click on the card.</summary>
        public bool Dismiss(long cardId)
        {
            lock (_gate)
            {
                if (!IsLive(cardId))
                    return false;
                _phase = Phase.Closing;
                return RequestRender();
            }
        }

        /// <summary>The window for this card has closed; its counters are gone.</summary>
        public void CardClosed(long cardId)
        {
            lock (_gate)
            {
                if (cardId == _cardId && (_phase == Phase.Visible || _phase == Phase.Closing))
                    _phase = Phase.None;
            }
        }

        /// <summary>
        /// The state to render now. Checks the deadline first, so a render that runs late
        /// (UI thread was busy) fades an expired card instead of showing it again.
        /// </summary>
        public ActivityRender TakeRender()
        {
            lock (_gate)
            {
                _renderPending = false;
                if (_phase == Phase.Visible && Expired())
                    _phase = Phase.Closing;

                switch (_phase)
                {
                    case Phase.Visible:
                        return new ActivityRender(ActivityCardPhase.Visible, Snapshot());
                    case Phase.Closing:
                        return new ActivityRender(ActivityCardPhase.Closing, Snapshot());
                    default:
                        return new ActivityRender(ActivityCardPhase.Hidden, null);
                }
            }
        }

        private void StartCard(bool isStatus, Phase phase)
        {
            _cardId = ++_lastCardId;
            _phase = phase;
            _isStatus = isStatus;
            _succeeded = 0;
            _failed = 0;
            _images = 0;
            _title = null;
            _body = null;
            _latestSuccess = false;
            _hasFailure = false;
            _statusTextProvider = null;
            _hovering = false;
        }

        private void Rearm()
        {
            if (_hovering)
                return;
            int seconds;
            if (_isStatus)
                seconds = _statusSeconds;
            else
            {
                seconds = _idleSeconds();
                if (seconds <= 0)
                    seconds = DefaultIdleSeconds;
            }
            _deadline = _now() + TimeSpan.FromSeconds(seconds);
        }

        private bool Expired() => !_hovering && _now() >= _deadline;

        private bool IsLive(long cardId) => cardId == _cardId && _phase == Phase.Visible;

        private bool RequestRender()
        {
            if (_renderPending)
                return false;
            _renderPending = true;
            return true;
        }

        private ActivitySnapshot Snapshot() => new ActivitySnapshot(
            _cardId, _isStatus, _succeeded, _failed, _images, _title, _body, _latestSuccess, _hasFailure,
            _statusTextProvider);
    }
}
