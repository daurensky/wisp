"""Summarize same-runner measurements without treating timing noise as a speedup."""
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
series = {label: [json.loads(p.read_text(encoding="utf-8-sig"))
                  for p in sorted(root.glob(f"{label}-*.json"))]
          for label in ("baseline", "current")}
for label, samples in series.items():
    if not samples or any(item.get("status") != "success" for item in samples):
        raise SystemExit(f"Missing/failed measurements: {label}")
    if any(item.get("schema") != 2 or item.get("idle_settling_ms") != 1500 for item in samples):
        raise SystemExit(f"Expected schema-2 measurements with 1500 ms idle settling: {label}")


def read(item, path):
    for key in path.split("."):
        item = item[key]
    return item


metrics = [
    ("First process to Home ready", "process_start_to_home_ready_ms", "ms", "first"),
    ("Repeated process to Home ready", "process_start_to_home_ready_ms", "ms", "repeat"),
    ("Main construction", "main_construction_ms", "ms", "all"),
    ("Settled cached Home CPU on hosted runner (one logical core)", "home_idle.cpu_percent_one_logical_core", "%", "all"),
    ("Home idle private memory", "home_idle.memory.private_bytes", "MiB", "all"),
    ("Home idle working set", "home_idle.memory.working_set_bytes", "MiB", "all"),
    ("Server switch median", "server_switch.median_ms", "ms", "all"),
    ("Server switch p95", "server_switch.p95_ms", "ms", "all"),
    ("24 server switches allocation", "server_switch.allocated_bytes", "MiB", "all"),
    ("Channel switch median (100 cached messages)", "channel_switch_100_cached_messages.median_ms", "ms", "all"),
    ("Channel switch p95 (100 cached messages)", "channel_switch_100_cached_messages.p95_ms", "ms", "all"),
    ("32 channel switches allocation", "channel_switch_100_cached_messages.allocated_bytes", "MiB", "all"),
    ("Unchanged presence UI refresh median", "unchanged_presence_ui_refresh.median_ms", "ms", "all"),
    ("Unchanged presence UI refresh p95", "unchanged_presence_ui_refresh.p95_ms", "ms", "all"),
    ("32 presence UI refreshes allocation", "unchanged_presence_ui_refresh.allocated_bytes", "MiB", "all"),
    ("1000-input bounded history load", "long_history_load.median_ms", "ms", "all"),
    ("Bounded history scroll median", "long_history_scroll.median_ms", "ms", "all"),
    ("Bounded history scroll p95", "long_history_scroll.p95_ms", "ms", "all"),
    ("48 scroll steps allocation", "long_history_scroll.allocated_bytes", "MiB", "all"),
    ("History retained rows", "history_retained_count", "rows", "all"),
    ("History maximum realized presenters", "history_max_realized_presenters", "rows", "all"),
    ("After 20 server switches private memory", "after_20_server_switches_memory.private_bytes", "MiB", "all"),
    ("Settled cached chat CPU on hosted runner (one logical core)", "chat_idle.cpu_percent_one_logical_core", "%", "all"),
]
summary = {"schema": 2, "baseline_revision": series["baseline"][0]["revision"],
           "current_revision": series["current"][0]["revision"], "metrics": [],
           "limits": series["current"][0]["unavailable"],
           "interpretation": "Descriptive same-runner samples, not statistical proof of speedup. No forced GC or OS-cache flush."}
lines = ["# Wisp cached-UI measurements", "",
         f"Baseline `{summary['baseline_revision']}`; current `{summary['current_revision']}`.", "",
         "Both versions ran on the same Windows runner with identical probe source, fixture and window size. "
         "Process order alternates; three independent processes per version are used by default. "
         "This is a cached local-UI probe, not online login or an OS cold-cache launch. "
         "Latency includes action, layout and dispatcher work through ApplicationIdle, not physical presentation latency. "
         "Long history supplies 1000 messages to the application's existing bounded list; retained and realized counts are reported. "
         "Managed allocation/GC counters and process CPU/private/working-set counters are sampled without GC.Collect(). "
         "CPU intervals follow 1500 ms without navigation so the 800 ms scroll-chrome timer can settle; "
         "these hosted-runner observations do not establish physical-device idle CPU. "
         "Raw idle samples include window/focus state and OS-thread CPU snapshots bracketing each interval. "
         "Thread deltas include snapshot overhead and cannot fully attribute CPU from new/exited/unreadable threads; they contain no stack profiling.", "",
         "| Metric | Baseline | Current | Unit |",
         "|---|---:|---:|---|"]
for title, path, unit, selection in metrics:
    row = {"metric": title, "path": path, "unit": unit}
    for label in series:
        selected = series[label][:1] if selection == "first" else series[label][1:] if selection == "repeat" else series[label]
        values = [read(item, path) / (1024 * 1024 if unit == "MiB" else 1) for item in selected]
        row[label] = statistics.median(values)
        row[label + "_samples"] = values
    summary["metrics"].append(row)
    lines.append(f"| {title} | {row['baseline']:.3f} | {row['current']:.3f} | {unit} |")
lines += ["", "Do not infer a speedup or a memory leak from small/noisy differences in these short samples. "
          "The raw JSON contains individual latency samples, GC counts, environment and memory checkpoints.", "",
          "Unavailable here:", ""] + ["- " + item for item in summary["limits"]]
(root / "comparison.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
(root / "comparison.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
print("\n".join(lines))
