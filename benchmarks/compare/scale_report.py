"""Builds the scaling report (Markdown, plus scale-data.json for charts) from the results written by scale.sh."""

import json
import re
import sys
from pathlib import Path

NAMES = {"fake": "Fake LLM (floor)", "ume": "Ume gateway", "eneo": "eneo v2.2.1"}
APP = {"ume": "ume-gateway", "eneo": "eneo-backend"}


def read_runs(folder: Path):
    runs = []
    for path in sorted(folder.glob("*.json")):
        m = re.fullmatch(r"(load)-(\w+)-vus_(\d+)\.json", path.name) or re.fullmatch(r"(cores)-(\w+)-cores_(\d+)-vus_(\d+)\.json", path.name)
        if not m:
            continue
        run = json.loads(path.read_text())
        cpu = path.with_name(path.stem + ".cpu.json")
        run["cpu"] = json.loads(cpu.read_text()) if cpu.exists() else {}
        run.update(kind=m[1], platform=m[2], x=int(m[3]), clients=int(m[4]) if m[1] == "cores" else int(m[3]))
        app = run["cpu"].get(APP.get(m[2], ""), {})
        run["cpu_ms_per_req"] = app.get("cpu_ns", 0) / 1e6 / run["requests"] if run["requests"] and app else None
        runs.append(run)
    return runs


def summary(run):
    d = run["duration_ms"]
    return {
        "rps": run["rps"], "median": d["med"], "p95": d["p(95)"], "p99": d["p(99)"],
        "ttfb": run["ttfb_ms"]["med"], "errors": run.get("errorRate", 0), "cpu_ms_per_req": run["cpu_ms_per_req"],
        "clients": run["clients"],
    }


def fmt_ms(v):
    return f"{v / 1000:.1f} s" if v >= 1000 else f"{v:.1f}" if v >= 10 else f"{v:.2f}"


def main(folder: Path) -> None:
    runs = read_runs(folder)
    data = {"load": {}, "cores": {}}
    for r in runs:
        if r["kind"] == "load":
            data["load"].setdefault(r["platform"], {})[r["x"]] = summary(r)
        else:
            # Resources: keep every concurrency, and the peak (best throughput with < 1 % errors) per core count.
            data.setdefault("cores_all", {}).setdefault(r["platform"], {}).setdefault(r["x"], []).append(summary(r))
    for platform, by_cores in data.get("cores_all", {}).items():
        for n, results in by_cores.items():
            clean = [s for s in results if s["errors"] < 0.01] or results
            data["cores"].setdefault(platform, {})[n] = max(clean, key=lambda s: s["rps"])
    (folder / "scale-data.json").write_text(json.dumps(data, indent=2))

    stream = next((r["stream"] for r in runs), True)
    print(f"# Scaling: Ume gateway vs. eneo ({'streaming' if stream else 'non-streaming'} chat, same fake LLM)\n")

    load = data["load"]
    print("## Load: concurrent clients on fixed resources (4 hardware threads = 2 physical cores each)\n")
    print("| Clients | Fake LLM req/s | Ume req/s | Ume median ms | Ume p99 ms | Ume errors | eneo req/s | eneo median ms | eneo p99 ms | eneo errors |")
    print("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for vus in sorted({x for p in load.values() for x in p}):
        cells = [str(vus)]
        f = load.get("fake", {}).get(vus)
        cells.append(f"{f['rps']:.0f}" if f else "–")
        for p in ("ume", "eneo"):
            s = load.get(p, {}).get(vus)
            cells += [f"{s['rps']:.0f}", fmt_ms(s["median"]), fmt_ms(s["p99"]), f"{s['errors']:.1%}"] if s else ["–"] * 4
        print("| " + " | ".join(cells) + " |")

    cores = data["cores"]
    print("\n## Resources: peak throughput per number of application hardware threads (vCPUs)\n")
    print("Each row is the best throughput (with < 1 % errors) over several client counts, on a fresh stack. Speed-up is")
    print("relative to one thread; efficiency is speed-up divided by threads (100 % = linear).\n")
    print("| vCPUs | Ume peak req/s | at clients | Ume speed-up | Ume efficiency | Ume CPU ms/req | Ume median ms | eneo peak req/s | at clients | eneo speed-up | eneo efficiency | eneo CPU ms/req | eneo median ms |")
    print("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for n in sorted({x for p in cores.values() for x in p}):
        cells = [str(n)]
        for p in ("ume", "eneo"):
            s = cores.get(p, {}).get(n)
            base = cores.get(p, {}).get(min(cores.get(p, {}) or [n]))
            if not s:
                cells += ["–"] * 6
                continue
            speedup = s["rps"] / base["rps"] if base and base["rps"] else 0
            cells += [f"{s['rps']:.0f}", str(s["clients"]), f"{speedup:.2f}×", f"{speedup / n:.0%}",
                      f"{s['cpu_ms_per_req']:.2f}" if s["cpu_ms_per_req"] is not None else "–", fmt_ms(s["median"])]
        print("| " + " | ".join(cells) + " |")
    print()


if __name__ == "__main__":
    main(Path(sys.argv[1] if len(sys.argv) > 1 else "results/scale"))
