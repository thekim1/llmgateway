"""Seed the eneo tenant with a default completion model wired to the shared fake LLM.

Adapted from eneo's own e2e/seed.py (v2.2.1): same tables and fields, but the endpoint and model name come from the
environment so eneo and the Ume gateway call the same fake upstream with the same model name. Runs after
init_db.py (which migrates and creates the tenant and user). Idempotent.

Credentials are plaintext on purpose: the stack runs with ENCRYPTION_KEY unset and TENANT_CREDENTIALS_ENABLED=false,
exactly like eneo's e2e stack. The fake upstream needs no real key.
"""

import asyncio
import os

from sqlalchemy import select

from eneo.database.database import sessionmanager
from eneo.database.tables.ai_models_table import CompletionModels
from eneo.database.tables.model_providers_table import ModelProviders
from eneo.database.tables.tenant_table import Tenants
from eneo.main.config import get_settings

ENDPOINT = os.environ["FAKE_LLM_ENDPOINT"]
MODEL_NAME = os.environ.get("FAKE_LLM_MODEL", "fake-chat")
# A virtual key when the endpoint is the Ume gateway (eneo behind the gateway); the fake LLM ignores it.
API_KEY = os.environ.get("FAKE_LLM_API_KEY") or "fake-key"
TENANT_NAME = os.environ["DEFAULT_TENANT_NAME"]
PROVIDER_NAME = "Fake LLM"


async def main() -> None:
    sessionmanager.init(get_settings().database_url)
    async with sessionmanager.session() as session, session.begin():
        tenant_id = (
            await session.execute(select(Tenants.id).where(Tenants.name == TENANT_NAME))
        ).scalar_one()

        already = (
            await session.execute(
                select(ModelProviders.id).where(
                    ModelProviders.tenant_id == tenant_id,
                    ModelProviders.name == PROVIDER_NAME,
                )
            )
        ).scalar_one_or_none()
        if already:
            print("[seed] fake LLM model already present, skipping", flush=True)
            return

        provider = ModelProviders(
            tenant_id=tenant_id,
            name=PROVIDER_NAME,
            provider_type="openai",
            credentials={"api_key": API_KEY, "endpoint": ENDPOINT},
            config={"endpoint": ENDPOINT},
            is_active=True,
        )
        session.add(provider)
        await session.flush()

        session.add(
            CompletionModels(
                name=MODEL_NAME,
                nickname="Fake LLM",
                max_input_tokens=8192,
                max_output_tokens=2048,
                family="openai",
                stability="stable",
                hosting="usa",
                org="OpenAI",
                vision=False,
                reasoning=False,
                supports_tool_calling=False,
                base_url=ENDPOINT,
                litellm_model_name=MODEL_NAME,
                tenant_id=tenant_id,
                provider_id=provider.id,
                is_enabled=True,
                is_default=True,
            )
        )
        print("[seed] created fake LLM provider + default completion model", flush=True)


if __name__ == "__main__":
    asyncio.run(main())
