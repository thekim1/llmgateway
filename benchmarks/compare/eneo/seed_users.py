"""Give the eneo compare tenant a realistic number of users, so the benchmark does not run against an empty database.

Runs inside the eneo-backend container (after init_db.py and seed.py):

    docker compose cp eneo/seed_users.py eneo-backend:/tmp/seed_users.py
    docker compose exec -T -e USERS=10000 eneo-backend python /tmp/seed_users.py

1. Inserts USERS users the way UserService.register does for an admin-created (SSO-style, passwordless) user: the user
   row, the tenant's default role (User) and a settings row. One INSERT ... SELECT per table, so it takes seconds.
2. Signs in as each user with a token from eneo's own AuthService and lets eneo do the rest through its API: opening
   the personal space creates it with its default assistant, and QUESTIONS messages per user leave chat history.

Idempotent for step 1 (existing benchmark users are kept); step 2 is skipped with QUESTIONS=0 and SPACES=false.
"""

import asyncio
import os
import time
from types import SimpleNamespace

import httpx
from sqlalchemy import text

from eneo.authentication.auth_service import AuthService
from eneo.database.database import sessionmanager
from eneo.main.config import get_settings

USERS = int(os.environ.get("USERS", "10000"))
QUESTIONS = int(os.environ.get("QUESTIONS", "1"))
SPACES = os.environ.get("SPACES", "true") == "true"
CONCURRENCY = int(os.environ.get("CONCURRENCY", "16"))
TENANT_NAME = os.environ["DEFAULT_TENANT_NAME"]
BASE = "http://localhost:8000/api/v1"
QUESTION = "Vilka handlingar behöver jag skicka in för att ansöka om bygglov för ett uterum?"


async def insert_users() -> list[SimpleNamespace]:
    async with sessionmanager.session() as session, session.begin():
        tenant_id, role_id = (
            await session.execute(
                text("SELECT id, default_role_id FROM tenants WHERE name = :name"), {"name": TENANT_NAME}
            )
        ).one()
        await session.execute(
            text(
                """
                INSERT INTO users (username, email, tenant_id, used_tokens, state)
                SELECT 'benchuser' || lpad(g::text, 5, '0'), 'benchuser' || lpad(g::text, 5, '0') || '@example.com',
                       :tenant_id, 0, 'active'
                FROM generate_series(1, :n) g
                ON CONFLICT DO NOTHING
                """
            ),
            {"tenant_id": tenant_id, "n": USERS},
        )
        await session.execute(
            text(
                """
                INSERT INTO users_roles (user_id, role_id)
                SELECT u.id, :role_id FROM users u
                WHERE u.username LIKE 'benchuser%'
                  AND NOT EXISTS (SELECT 1 FROM users_roles ur WHERE ur.user_id = u.id)
                """
            ),
            {"role_id": role_id},
        )
        await session.execute(
            text(
                """
                INSERT INTO settings (user_id)
                SELECT u.id FROM users u
                WHERE u.username LIKE 'benchuser%'
                  AND NOT EXISTS (SELECT 1 FROM settings s WHERE s.user_id = u.id)
                """
            )
        )
        rows = (
            await session.execute(
                text(
                    "SELECT id, tenant_id, email, username, credential_version FROM users "
                    "WHERE username LIKE 'benchuser%' ORDER BY username"
                )
            )
        ).all()
    return [SimpleNamespace(**row._mapping) for row in rows]


async def use_eneo(users: list[SimpleNamespace]) -> None:
    auth = AuthService()
    queue: asyncio.Queue[SimpleNamespace] = asyncio.Queue()
    for user in users:
        queue.put_nowait(user)
    failures = 0
    done = 0
    started = time.monotonic()

    async def worker(client: httpx.AsyncClient) -> None:
        nonlocal failures, done
        while not queue.empty():
            user = queue.get_nowait()
            headers = {"Authorization": f"Bearer {auth.create_access_token_for_user(user)}"}
            space = await client.get(f"{BASE}/spaces/type/personal/", headers=headers)
            if space.status_code != 200:
                failures += 1
                continue
            assistant_id = space.json()["default_assistant"]["id"]
            for _ in range(QUESTIONS):
                answer = await client.post(
                    f"{BASE}/conversations/",
                    json={"assistant_id": assistant_id, "question": QUESTION, "stream": False},
                    headers=headers,
                )
                if answer.status_code != 200:
                    failures += 1
            done += 1
            if done % 1000 == 0:
                print(f"[seed-users] {done}/{len(users)} users used eneo ({time.monotonic() - started:.0f} s)", flush=True)

    async with httpx.AsyncClient(timeout=120) as client:
        await asyncio.gather(*(worker(client) for _ in range(CONCURRENCY)))
    print(f"[seed-users] personal spaces and {QUESTIONS} message(s) per user done, {failures} failures", flush=True)


async def main() -> None:
    sessionmanager.init(get_settings().database_url)
    users = await insert_users()
    print(f"[seed-users] {len(users)} benchmark users in tenant {TENANT_NAME}", flush=True)
    if SPACES or QUESTIONS:
        await use_eneo(users)
    await sessionmanager.close()


if __name__ == "__main__":
    asyncio.run(main())
