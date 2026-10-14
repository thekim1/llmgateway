"""Builds a Markdown table from the stream probe summaries written by latency.sh."""

import json
import sys
from pathlib import Path

NAMES = {"fake": "Fake LLM directly (floor)", "ume": "Ume LLM gateway", "eneo": "eneo v2.2.1"}
APP = {"ume": "ume-gateway", "eneo": "eneo-backend"}


def main(folder: Path) -> None:
    runs = []
    for path in sorted(folder.glob("*-vus_*.json")):
        if path.name.endswith(".cpu.json"):
            continue
        run = json.loads(path.read_text())
        cpu = path.with_name(path.stem + ".cpu.json")
        run["cpu"] = json.loads(cpu.read_text()) if cpu.exists() else {}
        runs.append(run)
    if not runs:
        print("No results found.")
        return

    floor = {r["vus"]: r for r in runs if r["target"] == "fake"}
    print(f"# Streaming latency against a fake LLM with real-provider timing ({folder.name})\n")
    sample = runs[0]
    if sample.get("payload") == "document":
        print(f"Request: a ten-page document in a five-message conversation ({sample.get('request_bytes', 0) / 1024:.0f} KB of JSON).\n")
    print("Client-side, per streamed chat message. TTFT = time to the first event with answer text. Added = platform")
    print("median minus the fake LLM's own median at the same load. Gap = time between consecutive answer events.\n")
    print("| Clients | Platform | Streams/s | Errors | TTFT p50 | TTFT p95 | TTFT p99 | TTFT added | Gap p50 | Gap p99 | Gap max | Max gap per stream p95 | Gaps ≥ 250 ms | Gaps ≥ 1 s | Whole stream p50 | Whole p95 | Whole added | CPU ms/stream (app) |")
    print("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for vus in sorted({r["vus"] for r in runs}):
        for target in ("fake", "ume", "eneo"):
            run = next((r for r in runs if r["target"] == target and r["vus"] == vus), None)
            if run is None:
                continue
            t, g, w, m = run["ttft_ms"], run["gap_ms"], run["total_ms"], run["max_gap_per_stream_ms"]
            base = floor.get(vus)
            added_t = f"{t['p50'] - base['ttft_ms']['p50']:.1f}" if base and target != "fake" else "–"
            added_w = f"{w['p50'] - base['total_ms']['p50']:.1f}" if base and target != "fake" else "–"
            app = APP.get(target)
            ok = run["requests"] - run["errors"]
            cpu = f"{run['cpu'][app]['cpu_ns'] / 1e6 / ok:.2f}" if app and app in run["cpu"] and ok else "–"
            print(
                f"| {vus} | {NAMES[target]} | {run['rps']:.1f} | {run['errors']} | {t['p50']:.1f} | {t['p95']:.1f} | {t['p99']:.1f} | {added_t} "
                f"| {g['p50']:.1f} | {g['p99']:.1f} | {g['max']:.1f} | {m['p95']:.1f} | {run['gaps_over_250ms']} | {run['gaps_over_1s']} "
                f"| {w['p50']:.0f} | {w['p95']:.0f} | {added_w} | {cpu} |"
            )
    errors = [r for r in runs if r.get("first_error")]
    if errors:
        print("\nFirst error per scenario:\n")
        for r in errors:
            print(f"- {r['target']} at {r['vus']} clients: {r['first_error']}")


if __name__ == "__main__":
    main(Path(sys.argv[1] if len(sys.argv) > 1 else "results/latency/realistic"))
