using System;
using System.Diagnostics;
using System.Text;

/// <summary>Fail real-window integration checks when a binding path or conversion fails silently.</summary>
internal sealed class WpfBindingErrors : TraceListener
{
    private readonly TraceSource _source = PresentationTraceSources.DataBindingSource;
    private readonly SourceLevels _previous;
    private readonly StringBuilder _errors = new StringBuilder();
    public WpfBindingErrors()
    {
        _previous = _source.Switch.Level;
        _source.Switch.Level = SourceLevels.Error;
        _source.Listeners.Add(this);
    }
    public override void Write(string message) => _errors.Append(message);
    public override void WriteLine(string message) => _errors.AppendLine(message);
    public void AssertClean()
    {
        if (_errors.Length > 0) throw new Exception("WPF binding errors: " + _errors);
    }
    protected override void Dispose(bool disposing)
    {
        _source.Listeners.Remove(this);
        _source.Switch.Level = _previous;
        base.Dispose(disposing);
    }
}
