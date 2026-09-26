using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using RvtMcp.Plugin.Views.Settings;

/// <summary>In-memory stand-in for the Revit adapter: same staged/immediate split, no disk.</summary>
internal sealed class FakeSettings : ISettingsPresentation
{
    private int _idle = 20;
    private bool _cache;
    private bool _persist;
    private int _hours = 4;

    public int SavedIdle = 20;
    public bool SavedCache;
    public bool SavedPersist;
    public int SavedHours = 4;
    /// <summary>Config key whose Apply write should fail.</summary>
    public string FailApplyKey;
    public int CopyCount;
    public int Port = 49891;
    public bool Disposed;

    public event PropertyChangedEventHandler PropertyChanged;
    public event EventHandler LanguageChanged;

    public int PropertyChangedSubscribers => PropertyChanged?.GetInvocationList().Length ?? 0;
    public int LanguageChangedSubscribers => LanguageChanged?.GetInvocationList().Length ?? 0;

    public SettingsSnapshot Snapshot { get; } = new SettingsSnapshot { LocaleSource = "Saved setting", LocaleEffective = "en" };
    public string SelectedLanguage { get; private set; } = "auto";
    public string EffectiveLanguage => "en";
    public bool IsLanguageWriteDisabled => false;
    public bool ToastEnabled { get; private set; } = true;
    public int ToastIdleSeconds { get => _idle; set { _idle = value; Changed(); } }
    public bool CacheSendCodeBodies { get => _cache; set { _cache = value; Changed(); } }
    public bool PersistSendCodeBodies { get => _persist; set { _persist = value; Changed(); } }
    public int PersistSendCodeBodiesHours { get => _hours; set { _hours = value; Changed(); } }
    public bool IsDirty => _idle != SavedIdle || _cache != SavedCache || _persist != SavedPersist || _hours != SavedHours;
    public string ImmediateWarning { get; private set; }
    public string ImmediateWarningKey { get; private set; }
    public string RevitYear { get; set; } = "2024";
    public string TransportKind { get; set; } = "TCP";
    public bool IsListenerRunning { get; set; } = true;
    public bool IsClientConnected { get; set; } = true;
    public string ConnectionState => IsClientConnected ? "Connected" : IsListenerRunning ? "Waiting for MCP client" : "Listener stopped";
    public bool CanCopyPort => TransportKind == "TCP" && IsListenerRunning;
    public string PortText => CanCopyPort ? Port.ToString() : "Not applicable";

    public void SetToastEnabled(bool enabled)
    {
        ToastEnabled = enabled;
        Changed();
    }

    public void SetLanguage(string language)
    {
        SelectedLanguage = language;
        Changed();
    }

    public void SetListenerRunning(bool running)
    {
        IsListenerRunning = running;
        if (!running) IsClientConnected = false;
        Changed();
    }

    public void RestartListener()
    {
        if (!IsListenerRunning) return;
        Port++;
        IsClientConnected = false;
        Changed();
    }

    public void CopyPort()
    {
        if (CanCopyPort) CopyCount++;
    }

    public SettingsApplyReport Apply()
    {
        var report = new SettingsApplyReport();
        void Commit(string key, Action save)
        {
            if (key == FailApplyKey)
            {
                var message = key + ": could not be saved";
                report.Failed.Add(message);
                report.FieldErrors[key] = message;
                return;
            }
            save();
            report.Applied.Add(key);
        }
        if (_idle != SavedIdle) Commit("toastIdleSeconds", () => SavedIdle = _idle);
        if (_cache != SavedCache) Commit("cacheSendCodeBodies", () => SavedCache = _cache);
        if (_persist != SavedPersist) Commit("persistSendCodeBodies", () => SavedPersist = _persist);
        if (_hours != SavedHours) Commit("persistSendCodeBodiesHours", () => SavedHours = _hours);
        Changed();
        return report;
    }

    public void Cancel()
    {
        _idle = SavedIdle;
        _cache = SavedCache;
        _persist = SavedPersist;
        _hours = SavedHours;
        Changed();
    }

    public void SetImmediateWarning(string key, string message)
    {
        ImmediateWarningKey = key;
        ImmediateWarning = message;
        Changed();
    }

    public void RaiseConnectionChanged() => Changed();
    public void RaiseLanguageChanged() => LanguageChanged?.Invoke(this, EventArgs.Empty);

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public void Dispose() => Disposed = true;
}

internal sealed class FakeTools : ISettingsToolsPresentation
{
    private static readonly (string Name, string Description, string Source, string Timeout)[] Sample =
    {
        ("revit_get_current_view_info", "Read the active view: type, scale, level and crop state.", "Built-in", "60 s"),
        ("revit_ai_element_filter", "Find elements by category, parameter values and spatial bounds.", "Built-in", "60 s"),
        ("revit_create_level", "Create levels at the given elevations in millimetres.", "Built-in", "60 s"),
        ("revit_open_model", "Open a .rvt, .rte or .rfa and make it active, or open it in the background.", "Built-in", "600 s (1–900 s)"),
        ("revit_export_pdf", "Export sheets or views to PDF with the chosen print settings.", "Built-in", "60 s"),
        ("revit_send_code_to_revit", "Run a C# snippet inside Revit as an escape hatch for unsupported workflows.", "Built-in", "60 s"),
        ("revit_switch_target", "Point the server at another running Revit year.", "Built-in", "—"),
        ("revit_tag_all_rooms", "Tag every untagged room in the active view.", "Built-in", "60 s"),
        ("wall_area_report", "Sum wall areas by type and level for the active model.", "Baked", "60 s"),
        ("door_fire_rating_audit", "List doors whose fire rating is missing or inconsistent with the wall.", "Baked", "60 s"),
    };

    public bool Disposed;
    public int RefreshCount;

    public FakeTools()
    {
        Rows = new ObservableCollection<ToolRow>();
        SortBy("Name");
    }

    public event PropertyChangedEventHandler PropertyChanged;
    public ObservableCollection<ToolRow> Rows { get; }
    public string CountsText => "Built-in: 230 / 230 · Baked: 2";
    public string StatusText => string.Empty;
    public string StatusHelpText => null;
    public string SortColumn { get; private set; }
    public bool SortAscending { get; private set; } = true;

    public void Refresh()
    {
        RefreshCount++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsText)));
    }

    public void SortBy(string column)
    {
        SortAscending = column == SortColumn ? !SortAscending : true;
        SortColumn = column;
        Func<(string Name, string Description, string Source, string Timeout), string> key = column switch
        {
            "Description" => t => t.Description,
            "Source" => t => t.Source,
            "Timeout" => t => t.Timeout,
            _ => t => t.Name,
        };
        var ordered = SortAscending ? Sample.OrderBy(key, StringComparer.Ordinal) : Sample.OrderByDescending(key, StringComparer.Ordinal);
        Rows.Clear();
        var no = 0;
        foreach (var tool in ordered)
            Rows.Add(new ToolRow { No = ++no, Name = tool.Name, Description = tool.Description, Source = tool.Source, TimeoutDisplay = tool.Timeout, TimeoutHelpText = "Server budget " + tool.Timeout + "; transport grace 5 s." });
    }

    public void Dispose() => Disposed = true;
}
