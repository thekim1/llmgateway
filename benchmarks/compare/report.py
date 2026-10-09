"""Builds a Markdown comparison from the k6 summaries and container CPU samples written by run.sh."""

import json
import sys
from pathlib import Path

NAMES = {"fake": "Fake LLM directly (floor)", "ume": "Ume LLM gateway", "eneo": "eneo v2.2.1"}
APP = {"ume": ["ume-gateway"], "eneo": ["eneo-backend"], "fake": []}
DATA = {"ume": ["ume-db", "ume-redis"], "eneo": ["eneo-db", "eneo-redis"], "fake": []}


def load(folder: Path):
    runs = []
    for path in sorted(folder.glob("*-stream_*-vus_*.json")):
        if path.name.endswith(".cpu.json"):
            continue
        result = json.loads(path.read_text())
        cpu_path = path.with_name(path.stem + ".cpu.json")
        result["cpu"] = json.loads(cpu_path.read_text()) if cpu_path.exists() else {}
        runs.append(result)
    return runs


def per_request(run, key):
    value = run["cpu"].get(key)
    if run["target"] == "fake":
        return "–"
    return f"{value / run['requests']:.1f}" if value is not None and run["requests"] else "–"


def cpu_ms_per_request(run, services):
    total = sum(run["cpu"].get(s, {}).get("cpu_ns", 0) for s in services)
    return total / 1e6 / run["requests"] if run["requests"] else 0


def main(folder: Path) -> None:
    runs = load(folder)
    if not runs:
        print("No results found.")
        return

    floor = {(r["stream"], r["vus"]): r for r in runs if r["target"] == "fake"}
    print("# Platform comparison against the same fake LLM\n")
    print("Overhead = median latency minus the fake LLM's own median at the same load. CPU is container CPU time per")
    print("request: the platform process, and its Postgres + Redis. Statements and commands include background work (e.g.")
    print("usage writers). All checks verify status 200 and the fake answer.\n")
    for stream in (False, True):
        print(f"## {'Streaming' if stream else 'Non-streaming'} chat completion\n")
        print("| Clients | Platform | Requests/s | Median ms | Overhead ms | p95 ms | p99 ms | TTFB median ms | CPU ms/req (app) | CPU ms/req (DB+Redis) | Postgres statements/req | Redis commands/req | App memory MB | Failed |")
        print("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
        for vus in sorted({r["vus"] for r in runs}):
            for target in ("fake", "ume", "eneo"):
                run = next((r for r in runs if r["target"] == target and r["stream"] == stream and r["vus"] == vus), None)
                if run is None:
                    continue
                d, t = run["duration_ms"], run["ttfb_ms"]
                base = floor.get((stream, vus))
                overhead = f"{d['med'] - base['duration_ms']['med']:.2f}" if base and target != "fake" else "–"
                app_mem = sum(run["cpu"].get(s, {}).get("memory_bytes", 0) for s in APP[target]) / 2**20
                print(
                    f"| {vus} | {NAMES[target]} | {run['rps']:.1f} | {d['med']:.2f} | {overhead} | {d['p(95)']:.2f} | {d['p(99)']:.2f} "
                    f"| {t['med']:.2f} | {cpu_ms_per_request(run, APP[target]) if APP[target] else 0:.3f} "
                    f"| {cpu_ms_per_request(run, DATA[target]) if DATA[target] else 0:.3f} "
                    f"| {per_request(run, 'pg_statements')} | {per_request(run, 'redis_commands')} "
                    f"| {app_mem:.0f} | {run['checksFailed']} |"
                )
        print()


if __name__ == "__main__":
    main(Path(sys.argv[1] if len(sys.argv) > 1 else "results"))
