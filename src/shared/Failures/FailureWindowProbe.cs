using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;

namespace RvtMcp.Plugin.Failures
{
    // Issue-16 phase C: windows that wait for the user without a Revit DialogBoxShowing event (add-in MessageBox, file dialog).
    // The probe only reads: it never clicks, posts, activates or closes a window. The only message it sends is WM_NULL,
    // which does nothing, to tell a dialog's message loop from busy work. See docs/controlled-failures.md.
    internal static partial class FailureControl
    {
        internal sealed class WindowTarget
        {
            internal string OperationId;
            internal PendingRequestLifecycle Lifecycle;
            internal readonly WindowOperationLog Log = new WindowOperationLog();
        }

        // Kept on the appearance: the window may close after its operation has ended.
        private sealed class WindowLogEntry { internal WindowOperationLog Log; internal object Token; }

        private sealed class ContextSnapshot { internal string Key, Title, ModelScopeKey; internal long Generation; }

        private static WindowTarget _windowTarget;
        private static ContextSnapshot _contextSnapshot;
        private static long _contextGeneration;
        private static readonly WindowModalTracker WindowTracker = new WindowModalTracker();
        private static WindowModalProbe _windowProbe;
        private static IntPtr _mainWindow;

        private static void StartWindowProbe(IntPtr mainWindow)
        {
            _mainWindow = mainWindow;
            if (Environment.GetEnvironmentVariable("BIMWRIGHT_RVT_WINDOW_PROBE") == "0")
            {
                App.DebugLog("Window probe disabled by BIMWRIGHT_RVT_WINDOW_PROBE=0");
                return;
            }
            try
            {
                OwnWindows.RegisterClassHandler();
                _windowProbe = new WindowModalProbe(() => _mainWindow, WindowTracker, OnWindowNew, OnWindowConfirmed, OnWindowClosed, OnWindowsOpen);
                _windowProbe.Start();
            }
            catch (Exception ex) { App.DebugLog("Window probe start failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void StopWindowProbe()
        {
            try { _windowProbe?.Stop(); } catch { }
            _windowProbe = null;
        }

        // A model opening, closing or view change makes a window's model uncertain until the next idle snapshot.
        private static void BumpContextGeneration() { Interlocked.Increment(ref _contextGeneration); }

        private static void UpdateContextSnapshot(Autodesk.Revit.DB.Document doc, string modelScopeKey)
        {
            string key = null, title = null;
            try { if (doc != null && doc.IsValidObject) { key = UserDialogBuffer.Key(doc.PathName, doc.Title); title = doc.Title; } }
            catch { key = null; }
            Volatile.Write(ref _contextSnapshot, new ContextSnapshot { Key = key, Title = title,
                ModelScopeKey = key == null ? null : modelScopeKey, Generation = Interlocked.Read(ref _contextGeneration) });
        }

        // The snapshot describes this window only if nothing changed the context since the window was first seen.
        private static ContextSnapshot SnapshotFor(WindowAppearance appearance)
        {
            var snapshot = Volatile.Read(ref _contextSnapshot);
            return snapshot != null && WindowModalTracker.ContextUnchanged(snapshot.Generation, appearance.ContextGeneration,
                Interlocked.Read(ref _contextGeneration)) ? snapshot : null;
        }

        private static WindowTarget BeginWindowTarget(Scope scope)
        {
            var target = new WindowTarget { OperationId = scope.Operation.OperationId, Lifecycle = scope.Lifecycle };
            Volatile.Write(ref _windowTarget, target);
            return target;
        }

        private static void EndWindowTarget(WindowTarget target)
        {
            if (target != null) Interlocked.CompareExchange(ref _windowTarget, null, target);
        }

        private static void OnWindowNew(WindowAppearance appearance)
        {
            appearance.ContextGeneration = Interlocked.Read(ref _contextGeneration);
        }

        private static void OnWindowConfirmed(WindowAppearance appearance, JObject content)
        {
            var dialog = new JObject
            {
                ["kind"] = "window", ["title"] = content.Value<string>("title") ?? "",
                ["text"] = content.Value<string>("text") ?? "", ["buttons"] = content["buttons"]?.DeepClone() ?? new JArray(),
                ["source"] = "win32"
            };
            var target = Volatile.Read(ref _windowTarget);
            if (target == null) { BlockOutsideMcp(appearance); return; }
            appearance.OperationId = target.OperationId;
            // Past the cap the window is still reported live below; the final receipt says how many were left out.
            var token = target.Log.Add(dialog, appearance.ContextGeneration, SnapshotFor(appearance)?.ModelScopeKey);
            appearance.Record = token == null ? null : new WindowLogEntry { Log = target.Log, Token = token };
            // Ledger first, so a status query after the receipt already reports the wait.
            var stored = (JObject)dialog.DeepClone(); stored["appearanceId"] = appearance.AppearanceId;
            try { Store?.MarkWindowWait(target.OperationId, stored); } catch { }
            var text = dialog.Value<string>("text");
            target.Lifecycle.EmitDialogWait(new JObject
            {
                ["dialogId"] = "", ["manual"] = true, ["dialog"] = dialog,
                ["message"] = FailureCapture.SafeText(string.IsNullOrEmpty(text) ? dialog.Value<string>("title") : text, 1024)
            });
        }

        private static void BlockOutsideMcp(WindowAppearance appearance)
        {
            PendingRequestLifecycle.SetWindowModal(appearance.AppearanceId, appearance.Title);
            PendingRequestLifecycle.FailQueued();
        }

        // Every tick: a confirmed window that no running command owns blocks Revit; its state is refreshed so it never outlives the probe.
        private static void OnWindowsOpen(IReadOnlyList<WindowAppearance> open)
        {
            var target = Volatile.Read(ref _windowTarget);
            foreach (var appearance in open)
                if (appearance.OperationId == null || target == null || target.OperationId != appearance.OperationId)
                    BlockOutsideMcp(appearance);
        }

        private static void OnWindowClosed(WindowAppearance appearance)
        {
            if (appearance.Kind != "window" || !appearance.Confirmed) return;
            PendingRequestLifecycle.ClearWindowModal(appearance.AppearanceId);
            if (appearance.OperationId != null)
            {
                if (appearance.Record is WindowLogEntry entry) entry.Log.Close(entry.Token, SnapshotFor(appearance) != null);
                try { Store?.ClearWindowWait(appearance.OperationId, appearance.AppearanceId); } catch { }
                return;
            }
            // Outside MCP: noted for the next call on that model, only when the model is certain.
            var snapshot = SnapshotFor(appearance);
            if (snapshot?.Key == null) return;
            var content = appearance.Content as JObject;
            var item = new JObject { ["kind"] = "window", ["title"] = content?.Value<string>("title") ?? appearance.Title ?? "" };
            var text = content?.Value<string>("text");
            if (!string.IsNullOrEmpty(text)) item["text"] = FailureCapture.SafeText(text, UserDialogBuffer.MaxText);
            try { UserDialogs.Add(snapshot.Key, snapshot.Title, item, DateTimeOffset.UtcNow); } catch { }
        }

        // UI thread, at completion: one observed incident per window whose model is certain. A window whose model is not
        // certain is reported in the receipt without a model; windows past the per-operation cap are counted.
        private static void RecordWindowIncidents(Scope scope)
        {
            var target = scope.Window;
            if (target == null) return;
            scope.OmittedWindows = target.Log.Omitted;
            target.Log.Finish(Interlocked.Read(ref _contextGeneration), out var withModel, out var withoutModel);
            foreach (var dialog in withoutModel)
                scope.WindowsWithoutModel.Add(new JObject { ["kind"] = "window", ["title"] = FailureCapture.SafeText(dialog.Value<string>("title") ?? "", 256),
                    ["text"] = FailureCapture.SafeText(dialog.Value<string>("text") ?? "", 320), ["answeredBy"] = "user" });
            foreach (var pair in withModel)
            {
                var dialog = pair.Key; var key = pair.Value;
                var title = FailureCapture.SafeText(dialog.Value<string>("title") ?? "", 256);
                var text = dialog.Value<string>("text");
                if (Store == null)
                {
                    scope.WindowsWithoutModel.Add(new JObject { ["kind"] = "window", ["title"] = title,
                        ["text"] = FailureCapture.SafeText(text ?? "", 320), ["answeredBy"] = "user" });
                    continue;
                }
                var incident = scope.NewIncident(null, "window", title, "observed", "window_observed");
                incident.ModelScopeKey = key; incident.Severity = "Window"; incident.MessageCount = 1;
                incident.OperationContext = "window";
                incident.Messages.Add(new RvtMcp.Failures.FailureMessage { DefinitionId = "window", Severity = "Window", Source = "window",
                    TransactionKey = title, Description = FailureCapture.SafeText(string.IsNullOrEmpty(text) ? title : text) });
                incident.NativeContextDescription = "A window without a Revit dialog event waited for the user during this operation. Revit does not report which button was chosen.";
                try { scope.Record(incident); } catch { scope.StorageFailure(); }
            }
        }
    }

    /// <summary>Handles of the add-in's own WPF windows (History, Settings, toasts…): never reported as Revit dialogs.</summary>
    internal static class OwnWindows
    {
        private static readonly ConcurrentDictionary<long, byte> Handles = new ConcurrentDictionary<long, byte>();
        private static int _registered;

        internal static bool Contains(IntPtr handle) => Handles.ContainsKey(handle.ToInt64());

        internal static void RegisterClassHandler()
        {
            if (Interlocked.Exchange(ref _registered, 1) == 1) return;
            System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Window), System.Windows.FrameworkElement.LoadedEvent,
                new System.Windows.RoutedEventHandler((sender, args) =>
                {
                    var window = sender as System.Windows.Window;
                    if (window == null || window.GetType().Assembly != typeof(OwnWindows).Assembly) return;
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    if (handle == IntPtr.Zero || !Handles.TryAdd(handle.ToInt64(), 0)) return;
                    window.Closed += (s, e) => Handles.TryRemove(handle.ToInt64(), out _);
                }), true);
        }
    }

    /// <summary>Background thread that lists the Revit process's top-level windows with Win32. It never touches the Revit API.</summary>
    internal sealed class WindowModalProbe
    {
        private const int TickMs = 250, MaxControls = 40, ReadBudgetMs = 300, MessageTimeoutMs = 200, MaxControlText = 300;
        private const uint WM_NULL = 0x0000, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E;
        private const uint SMTO_BLOCK = 0x0001, SMTO_ABORTIFHUNG = 0x0002;
        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;

        private readonly Func<IntPtr> _main;
        private readonly WindowModalTracker _tracker;
        private readonly Action<WindowAppearance> _onNew, _onClosed;
        private readonly Action<WindowAppearance, JObject> _onConfirmed;
        private readonly Action<IReadOnlyList<WindowAppearance>> _onOpen;
        private Thread _thread;
        private volatile bool _stopping;

        internal WindowModalProbe(Func<IntPtr> main, WindowModalTracker tracker, Action<WindowAppearance> onNew,
            Action<WindowAppearance, JObject> onConfirmed, Action<WindowAppearance> onClosed, Action<IReadOnlyList<WindowAppearance>> onOpen)
        { _main = main; _tracker = tracker; _onNew = onNew; _onConfirmed = onConfirmed; _onClosed = onClosed; _onOpen = onOpen; }

        internal void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "RvtMcp.WindowProbe" };
            _thread.Start();
        }

        internal void Stop()
        {
            _stopping = true;
            try { _thread?.Join(2000); } catch { }
        }

        private void Run()
        {
            try
            {
                while (!_stopping)
                {
                    Tick();
                    Thread.Sleep(TickMs);
                }
            }
            catch (Exception ex)
            {
                try { App.DebugLog("Window probe stopped after an error: " + ex.GetType().Name + ": " + ex.Message); } catch { }
            }
            finally
            {
                // Release every state this probe set: a stopped probe never leaves Revit reported as busy.
                foreach (var appearance in _tracker.Reset())
                    try { _onClosed(appearance); } catch { }
            }
        }

        private void Tick()
        {
            var main = _main();
            if (main == IntPtr.Zero || !IsWindow(main)) return;
            var uiThread = GetWindowThreadProcessId(main, out var pid);
            bool enabled = IsWindowEnabled(main);
            var windows = enabled ? new List<WindowSighting>() : Enumerate(main, pid, uiThread);
            var transitions = _tracker.Tick(DateTimeOffset.UtcNow, enabled, windows, Pumps);
            foreach (var appearance in transitions.New) Safe(() => _onNew(appearance));
            foreach (var appearance in transitions.Closed) Safe(() => _onClosed(appearance));
            foreach (var appearance in transitions.Confirmed)
            {
                var handle = new IntPtr(appearance.Handle);
                var title = Text(handle);
                if (title.Length > 0) appearance.Title = title;
                var content = Read(handle, appearance.Title);
                appearance.Content = content;
                Safe(() => _onConfirmed(appearance, content));
            }
            if (transitions.Open.Count > 0) Safe(() => _onOpen(transitions.Open));
        }

        private static void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { try { App.DebugLog("Window probe callback failed: " + ex.GetType().Name + ": " + ex.Message); } catch { } }
        }

        private static List<WindowSighting> Enumerate(IntPtr main, uint pid, uint uiThread)
        {
            var result = new List<WindowSighting>();
            EnumWindows((handle, _) =>
            {
                if (handle == main) return true;
                var thread = GetWindowThreadProcessId(handle, out var owner);
                if (owner != pid) return true;
                var sighting = new WindowSighting
                {
                    Handle = handle.ToInt64(), ClassName = ClassName(handle), Title = Text(handle),
                    Style = GetWindowLongPtr(handle, GWL_STYLE).ToInt64() & 0xFFFFFFFFL,
                    ExStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64() & 0xFFFFFFFFL,
                    SameUiThread = thread == uiThread, Visible = IsWindowVisible(handle), Enabled = IsWindowEnabled(handle),
                    IsOwn = OwnWindows.Contains(handle)
                };
                if (WindowModalTracker.IsCandidate(sighting)) sighting.HasProgressBar = HasProgressBar(handle);
                result.Add(sighting);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static bool HasProgressBar(IntPtr window)
        {
            bool found = false;
            EnumChildWindows(window, (child, _) =>
            {
                found = WindowModalTracker.IsProgressControl(ClassName(child), ClassName(GetParent(child)));
                return !found;
            }, IntPtr.Zero);
            return found;
        }

        // A dialog's message loop answers at once; a window over busy work does not.
        private static bool Pumps(WindowSighting window)
            => SendMessageTimeout(new IntPtr(window.Handle), WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_BLOCK | SMTO_ABORTIFHUNG, 100, out _) != IntPtr.Zero;

        // Title, static text and button labels only (never edit boxes or lists), bounded and masked.
        private static JObject Read(IntPtr dialog, string title)
        {
            var clock = Stopwatch.StartNew();
            var texts = new List<string>();
            var buttons = new JArray();
            int controls = 0;
            EnumChildWindows(dialog, (child, _) =>
            {
                if (controls >= MaxControls || clock.ElapsedMilliseconds > ReadBudgetMs) return false;
                var name = ClassName(child).ToLowerInvariant();
                bool button = name == "button" || name.Contains(".button.");
                bool label = name == "static" || name.Contains(".static.");
                if ((!button && !label) || !IsWindowVisible(child)) return true;
                controls++;
                var text = Text(child);
                if (text.Length == 0) text = TextByMessage(child, clock);
                text = text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
                if (text.Length == 0) return true;
                if (text.Length > MaxControlText) text = text.Substring(0, MaxControlText);
                if (button) buttons.Add(new JObject { ["text"] = FailureCapture.SafeText(text, MaxControlText) });
                else texts.Add(text);
                return true;
            }, IntPtr.Zero);
            return new JObject
            {
                ["title"] = FailureCapture.SafeText(title ?? "", 256),
                ["text"] = FailureCapture.SafeText(string.Join("\n", texts), 1024),
                ["buttons"] = buttons
            };
        }

        // Each wait gets only what is left of the read budget, so the whole read stays within ReadBudgetMs.
        private static uint MessageTimeout(long elapsedMs) => WindowModalTracker.RemainingTimeout(elapsedMs, ReadBudgetMs, MessageTimeoutMs);

        private static string TextByMessage(IntPtr control, Stopwatch clock)
        {
            var timeout = MessageTimeout(clock.ElapsedMilliseconds);
            if (timeout == 0 || SendMessageTimeout(control, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeout, out var length) == IntPtr.Zero) return "";
            var size = (int)Math.Min(Math.Max(length.ToInt64(), 0) + 1, 2048);
            if (size <= 1) return "";
            timeout = MessageTimeout(clock.ElapsedMilliseconds);
            if (timeout == 0) return "";
            var buffer = new StringBuilder(size);
            return SendMessageTimeoutText(control, WM_GETTEXT, new IntPtr(size), buffer, SMTO_ABORTIFHUNG, timeout, out _) == IntPtr.Zero ? "" : buffer.ToString();
        }

        private static string ClassName(IntPtr handle) { var buffer = new StringBuilder(256); GetClassName(handle, buffer, buffer.Capacity); return buffer.ToString(); }
        private static string Text(IntPtr handle) { var buffer = new StringBuilder(1024); InternalGetWindowText(handle, buffer, buffer.Capacity); return buffer.ToString(); }

        private delegate bool EnumProc(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr handle, StringBuilder text, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeoutText(IntPtr handle, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
    }
}
