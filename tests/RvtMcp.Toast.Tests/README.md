# WPF toast regression

Windows-only STA executable using the production toast window/manager and real WPF,
with no Revit dependency. Requires the .NET 10 SDK and an interactive Windows session.
It briefly shows test notifications and closes them on completion.

```powershell
dotnet run --project tests/RvtMcp.Toast.Tests -c Release
```

Exit code 0 requires both checks to pass:

- Position an unshown window (default `Top` / `Left` are `NaN`), show it, and let the WPF dispatcher evaluate animation clocks.
- Close a toast, insert another during the animated reflow, verify final stack positions, and enforce the four-toast cap.

Before the fix, the first check terminates with `AnimationException` / `DoubleAnimation cannot use default origin value of NaN`, matching the Revit 2027 crash dump. After the fix both checks pass. This is separate from the cross-platform xUnit suite and does not establish live Revit acceptance.
