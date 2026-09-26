# WPF toast regression

Windows-only STA executable using the production toast window/manager and real WPF,
with no Revit dependency. Requires the .NET 10 SDK and an interactive Windows session.
It briefly shows test notifications and closes them on completion.

```powershell
dotnet run --project tests/RvtMcp.Toast.Tests -c Release
```

Exit code 0 requires all checks to pass:

- Merge outcome and context into one body, preserving errors/capture hints and omitting blank or duplicate context.
- Verify the single activity card has one latest-result row, no thumbnail, and stable height while its counters update.
- Create the first card through `ActivityAggregator → McpToastManager`, proving the manager sets finite coordinates before `Show` and never animates from WPF's default `NaN` position.
- Filter a stationary pointer baseline, deliver 100 results into one card, update it without replaying the enter animation, replace a card during its fade, and close it synchronously.
- Park the card while the owner frame is unusable, restore it when usable, and turn it off during a fade without leaving a topmost window behind.
- Render a status card in the shared slot, exercise the × control, route a card click to the host callback, and dismiss the card after the callback.

Before the fix, the placement check terminated with `AnimationException` / `DoubleAnimation cannot use default origin value of NaN`, matching the Revit 2027 crash dump. This harness now exercises the single-card WPF path; it is separate from the cross-platform xUnit suite and does not establish live Revit acceptance.
