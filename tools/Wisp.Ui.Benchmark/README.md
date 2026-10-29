# Native cached-UI performance probe

This developer tool shows the actual WPF MainWindow with an isolated SQLite fixture: two servers, ten channels per server, twelve members, and 100 cached messages per text channel. It records process-first/repeated startup to Home, construction, two-second Home/chat idle CPU, private/working-set/managed memory, allocations and GC counts, server/channel dispatcher responsiveness, repeated unchanged online-roster UI refreshes, and long-history load/scroll with realized controls.

The long-history input contains 1000 messages. Current production retains at most 200 visual rows; the probe reports both input and actual retained/realized counts instead of claiming that 1000 controls are rendered.

`ui-performance.yml` copies the exact same probe source into a pinned baseline checkout and builds both versions. The script alternates process order on one Windows runner and retains raw samples plus comparison JSON/Markdown. It is not part of the shipping client, does not change production contracts, and does not use forced GC or modify Windows settings.

```powershell
dotnet build tools/Wisp.Ui.Benchmark -c Release
.\tools\Wisp.Ui.Benchmark\bin\Release\net8.0-windows10.0.22621.0\Wisp.Ui.Benchmark.exe --mode prepare --fixture C:\Temp\wisp-perf
.\tools\Wisp.Ui.Benchmark\bin\Release\net8.0-windows10.0.22621.0\Wisp.Ui.Benchmark.exe --fixture C:\Temp\wisp-perf --output C:\Temp\wisp-perf-results.json
```

“First process” does **not** imply an OS cold cache. Online auth/HTTP RTT, active GIF hovering, real microphone/media traffic, GPU encoding/capture, physical frames and monitors are not measured. Small differences on hosted runners are descriptive and cannot establish acceleration or prove leak freedom. Read each JSON's environment and limit fields before comparing. Keep this tool separate from unit/regression tests; timing results are not brittle pass/fail thresholds.
