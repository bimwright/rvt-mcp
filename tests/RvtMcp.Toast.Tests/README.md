# WPF toast regression

Windows-only STA executable using the production toast window/manager and real WPF,
with no Revit dependency. Requires the .NET 10 SDK and an interactive Windows session.
It briefly shows test notifications and closes them on completion.

```powershell
dotnet run --project tests/RvtMcp.Toast.Tests -c Release
```

Exit code 0 requires all checks to pass:

- Merge outcome and context into one body, preserving errors/capture hints and omitting blank or duplicate context.
- Verify the five-row activity card: the title names the gateway (`rvt-mcp`, or the supplied instance identity such as `rvt-mcp 2027`), the line under it names the latest tool, the Success · Failed · Capture counters are spread evenly with the first one lined up with the tool line, and the brand row is right-aligned. The card keeps its height for any result text.
- Odometer counters: only the digits that change roll, a new value continues the roll from where it is, a number that gains a digit widens gradually, a burst settles on the last value without resizing the card, and unchanged counters do not animate.
- Thumbnail: the capture sits centred on both axes in one fixed frame whatever its shape. The frame opens with the card growing around it, cross-fades to the next capture and fades out before the card closes. An interrupted close still ends fully open, and nothing animates when Windows animation effects are off. A path outside the allowlist never renders.
- Keep the × background transparent on hover, and crossfade the two wordmark layers only inside the moving hover band without dimming the whole logo.
- Honor Windows reduced-motion preferences and release counter/brand/thumbnail animation clocks on close.
- Create the first card through `ActivityAggregator → McpToastManager`, proving the manager sets finite coordinates before `Show` and never animates from WPF's default `NaN` position.
- Filter a stationary pointer baseline, deliver 100 results into one card, update it without replaying the enter animation, replace a card during its fade, and close it synchronously.
- Park the card while the owner frame is unusable, restore it when usable, and turn it off during a fade without leaving a topmost window behind.
- Render a status card in the shared slot, exercise the × control, route a card click to the host callback, and dismiss the card after the callback.

## Interactive preview

```powershell
dotnet run --project tests/RvtMcp.Toast.Tests -c Release -- --demo
```

Opens the production window/manager with simulated results (no Revit tool calls or model edits).
Motion is forced on, whatever Windows' animation setting says. Ten successes play automatically; two are
also counted as captures, one wide and one tall, to show the centring and the cross-fade. Controls replay
the sequence, produce a burst, add an error, show a status card, cycle the instance title, or reset.
Hover to inspect the brand sweep and pause the 60-second preview idle timer. Closing the controller
closes its toast.

Before the fix, the placement check terminated with `AnimationException` / `DoubleAnimation cannot use default origin value of NaN`, matching the Revit 2027 crash dump. This harness now exercises the single-card WPF path; it is separate from the cross-platform xUnit suite and does not establish live Revit acceptance.
