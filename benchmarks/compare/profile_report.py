"""Builds results/profile/report.md from profile.sh's output: CPU split, runtime counters and trace frames per layout.

The trace is dotnet-trace's sampled thread time: each sample says which managed frame a thread is in (leaf CPU_TIME:
in managed code or a runtime call such as a GC wait; UNMANAGED_CODE_TIME: in native code, e.g. epoll). It is wall clock,
not CPU, so the frame tables show where threads spend their time, as a share of all managed time.
"""
import collections
import csv
import json
import re
import sys

folder, layouts = sys.argv[1], sys.argv[2:]
COUNTERS = {
    'dotnet.gc.heap.total_allocated': ('Allocated per request', 'KB', 1 / 1024, True),
    'dotnet.gc.pause.time': ('Time in GC pauses', '%', 100 / 2, False),
    'dotnet.monitor.lock_contentions': ('Lock contentions/s', '', 1 / 2, False),
    'dotnet.thread_pool.work_item.count': ('Thread-pool work items per request', '', 1, True),
}


def short(name):
    return re.sub(r'\(.*', '', name).split('!')[-1]


def frames(path):
    """→ (self time of the frame under CPU_TIME, callers of it), in thread-ms, from a Speedscope file."""
    d = json.load(open(path))
    names = [f['name'] for f in d['shared']['frames']]
    cpu = names.index('CPU_TIME')
    leaf, callers = collections.Counter(), collections.defaultdict(collections.Counter)
    for p in d['profiles']:
        stack, last = [], None
        for e in p['events']:
            if last is not None and len(stack) > 1 and stack[-1] == cpu and e['at'] > last:
                dt, top = e['at'] - last, short(names[stack[-2]])
                leaf[top] += dt
                chain = [short(names[f]) for f in reversed(stack[:-2])]
                chain = [c for c in chain if not re.search(r'MoveNext|RunInternal|AsyncMethodBuilderCore', c)][:2]
                callers[top][' ← '.join(chain)] += dt
            last = e['at']
            if e['type'] == 'O':
                stack.append(e['frame'])
            elif e['frame'] in stack:
                del stack[len(stack) - 1 - stack[::-1].index(e['frame']):]
    return leaf, callers


data = {}
for name in layouts:
    k6 = json.load(open(f'{folder}/{name}-k6.json'))
    _, *v, ms = open(f'{folder}/{name}-cpu.txt').read().split()
    before, after = list(map(int, v[:3])), list(map(int, v[3:]))
    rps = k6['rps']
    seconds = int(ms) / 1000
    total, user, kernel = ((a - b) / 1e9 / seconds for a, b in zip(after, before))
    counters = collections.defaultdict(list)
    for row in csv.DictReader(open(f'{folder}/{name}-counters.csv')):
        key = row['Counter Name'].split(' ')[0]
        if key in COUNTERS and 'generation' not in row['Counter Name']:
            counters[key].append(float(row['Mean/Increment']))
    data[name] = dict(rps=rps, cores=total, user=user, kernel=kernel,
                      counters={k: sum(v) / len(v) for k, v in counters.items()},
                      trace=frames(f'{folder}/{name}.speedscope.json'))

out = ['# Inside the gateway at its peak (streaming chat)', '',
       '| | ' + ' | '.join(layouts) + ' |', '|---|' + '---:|' * len(layouts)]
row = lambda label, f: out.append(f'| {label} | ' + ' | '.join(f(data[n]) for n in layouts) + ' |')
row('Requests/s (whole run, profiling included)', lambda d: f"{d['rps']:.0f}")
row('Cores busy', lambda d: f"{d['cores']:.2f}")
row('CPU ms per request', lambda d: f"{d['cores'] / d['rps'] * 1000:.3f}")
row('– user mode', lambda d: f"{d['user'] / d['rps'] * 1000:.3f}")
row('– kernel', lambda d: f"{d['kernel'] / d['rps'] * 1000:.3f}")
for key, (label, unit, scale, per_request) in COUNTERS.items():
    row(label + (f' ({unit})' if unit else ''),
        lambda d: f"{d['counters'].get(key, 0) * scale / (d['rps'] * 2 if per_request else 1):.1f}")

out += ['', '## Where threads spend managed time (share of all managed thread time)', '',
        '| Frame | ' + ' | '.join(layouts) + ' |', '|---|' + '---:|' * len(layouts)]
share = {n: (d['trace'][0], sum(d['trace'][0].values())) for n, d in data.items()}
top = sorted({f for leaf, _ in share.values() for f, _ in leaf.most_common(15)}, key=lambda f: -share[layouts[-1]][0][f])
for f in top:
    out.append(f'| `{f[-90:].replace("|", "\\|")}` | ' + ' | '.join(f'{share[n][0][f] / share[n][1] * 100:.1f} %' for n in layouts) + ' |')

out += ['', '## Who waits on locks and wakes threads (last layout)', '']
callers = data[layouts[-1]]['trace'][1]
for f in ('System.Threading.Monitor.Enter_Slowpath', 'Interop+Kernel32.SetEvent', 'System.Threading.Thread.<PollGC>g__PollGCWorker|67_0'):
    total = sum(callers[f].values()) or 1
    out.append(f'- `{f}`')
    out += [f'  - {v / total * 100:.0f} % `{c}`' for c, v in callers[f].most_common(4)]

open(f'{folder}/report.md', 'w').write('\n'.join(out) + '\n')
print('\n'.join(out))
