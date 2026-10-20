"""Set eneo up for an external application, the way an administrator would through eneo's API, and print
"<assistant id> <api key>" on the last line.

Runs inside the eneo-backend container (lib.sh does it when ENEO_AUTH=apikey). As the default user (Owner):

1. creates a shared space "Benchmark API" and an assistant in it (eneo gives it the tenant's default model, the fake LLM);
2. creates a service API key (sk_, ownership "service": not tied to a person) scoped to that assistant, with write
   permission. eneo requires an IP allowlist or an expiry date for such keys: it gets a 90-day expiry. Its rate limit is
   raised to 100 000 000 requests an hour (the default for one assistant is 1 000), so the limit is checked on every
   request as in production but never reached.
"""

import os
from datetime import datetime, timedelta, timezone

import httpx

BASE = "http://localhost:8000/api/v1"

with httpx.Client(base_url=BASE, timeout=60) as client:
    login = client.post(
        "/users/login/token/",
        data={"username": os.environ["DEFAULT_USER_EMAIL"], "password": os.environ["DEFAULT_USER_PASSWORD"]},
    )
    login.raise_for_status()
    client.headers["Authorization"] = f"Bearer {login.json()['access_token']}"

    space = client.post("/spaces/", json={"name": "Benchmark API"})
    space.raise_for_status()
    assistant = client.post("/assistants/", json={"name": "Benchmark assistant", "space_id": space.json()["id"]})
    assistant.raise_for_status()
    assistant_id = assistant.json()["id"]

    key = client.post(
        "/api-keys",
        json={
            "name": "Benchmark service key",
            "key_type": "sk_",
            "permission": "write",
            "scope_type": "assistant",
            "scope_id": assistant_id,
            "ownership": "service",
            "rate_limit": 100_000_000,
            "expires_at": (datetime.now(timezone.utc) + timedelta(days=90)).isoformat(),
        },
    )
    key.raise_for_status()
    print(assistant_id, key.json()["secret"], flush=True)
