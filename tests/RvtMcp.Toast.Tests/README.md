# WPF toast regression

Windows-only STA executable using the production toast window/manager and real WPF,
with no Revit dependency. Requires the .NET 10 SDK and an interactive Windows session.
It briefly shows test notifications and closes them on completion.

```powershell
dotnet run --project tests/RvtMcp.Toast.Tests -c Release
```

Exit code 0 requires all checks to pass:

- Merge outcome and context into one body, preserving errors/capture hints and omitting blank or duplicate context.
- Verify the three-row activity card shows `RVT-MCP - {tool name}`, rolling Success / Failed / Capture counters, and a right-aligned brand; no thumbnail or repeated latest-result row.
- Exercise 280 ms vertical counter rolls, digit growth and burst replacement without resizing the card; unchanged counters do not animate.
- Keep the × background transparent on hover, and crossfade the two wordmark layers only inside the moving hover band without dimming the whole logo.
- Honor Windows reduced-motion preferences and release counter/brand animation clocks on close.
- Create the first card through `ActivityAggregator → McpToastManager`, proving the manager sets finite coordinates before `Show` and never animates from WPF's default `NaN` position.
- Filter a stationary pointer baseline, deliver 100 results into one card, update it without replaying the enter animation, replace a card during its fade, and close it synchronously.
- Park the card while the owner frame is unusable, restore it when usable, and turn it off during a fade without leaving a topmost window behind.
- Render a status card in the shared slot, exercise the × control, route a card click to the host callback, and dismiss the card after the callback.

## Interactive preview

```powershell
dotnet run --project tests/RvtMcp.Toast.Tests -c Release -- --demo
```

Opens the production window/manager with simulated results (no Revit tool calls or model edits).
Ten successes play automatically; two are also counted as captures. Controls replay the sequence,
produce a burst, add an error, show a status card, or reset. Hover to inspect the brand sweep and
pause the 60-second preview idle timer. Closing the controller closes its toast.

Before the fix, the placement check terminated with `AnimationException` / `DoubleAnimation cannot use default origin value of NaN`, matching the Revit 2027 crash dump. This harness now exercises the single-card WPF path; it is separate from the cross-platform xUnit suite and does not establish live Revit acceptance.
