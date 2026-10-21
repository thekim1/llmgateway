"""Sends requests until a stop file appears and reports every response other than the expected status.

Used by the rolling-update tests (compose-rolling-e2e.sh, kubernetes-e2e.sh) to show that replacing instances loses no
request. POSTs use a new connection each (their early 401 makes nginx close the client connection, which says nothing
about the gateway); GETs reuse keep-alive connections. Each request to the gateway leg still reuses the proxy's pool.

  python3 rolling-load.py HOST PORT PATH EXPECTED_STATUSES THREADS STOP_FILE [GET|POST] [CONNECT_ADDRESS]

EXPECTED_STATUSES is one status or several separated by commas (401,429: the gateway refuses the missing key, or, once
one address has sent Gateway:AuthFailuresPerMinute of them, throttles it; either way the gateway answered).
"""
import collections
import http.client
import os
import ssl
import sys
import threading
import time

host, port, path, threads, stop = sys.argv[1], int(sys.argv[2]), sys.argv[3], int(sys.argv[5]), sys.argv[6]
expect = {int(status) for status in sys.argv[4].split(",")}
method = sys.argv[7] if len(sys.argv) > 7 else "GET"
address = sys.argv[8] if len(sys.argv) > 8 else host
body = b'{"model":"x","messages":[{"role":"user","content":"hi"}]}'
context = ssl.create_default_context()
context.check_hostname = False
context.verify_mode = ssl.CERT_NONE  # test certificates; the point is availability, not identity
counts = collections.Counter()
unexpected = []
lock = threading.Lock()


def record(outcome):
    with lock:
        counts[outcome] += 1
        if outcome not in expect:
            unexpected.append((time.strftime("%H:%M:%S"), str(outcome)))


def worker():
    conn = None
    while not os.path.exists(stop):
        try:
            if conn is None:
                conn = http.client.HTTPSConnection(address, port, context=context, timeout=30)
            if method == "POST":
                conn.request("POST", path, body=body, headers={"Host": host, "Content-Type": "application/json", "Connection": "close"})
            else:
                conn.request("GET", path, headers={"Host": host})
            response = conn.getresponse()
            response.read()
            record(response.status)
            if method == "POST" or (response.getheader("connection") or "").lower() == "close":
                conn.close()
                conn = None
        except Exception as error:  # every failure counts; the test is that there are none
            record(type(error).__name__)
            if conn is not None:
                conn.close()
            conn = None
            time.sleep(0.05)


workers = [threading.Thread(target=worker) for _ in range(threads)]
for w in workers:
    w.start()
for w in workers:
    w.join()
print(f"{method} {host}{path}: {dict(counts)}")
for (second, outcome), n in sorted(collections.Counter(unexpected).items()):
    print(f"  {second} {outcome} x{n}")
sys.exit(1 if unexpected or not counts else 0)
