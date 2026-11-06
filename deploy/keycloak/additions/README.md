# Other systems' realm files

Put other systems' parts of realm `ume` here (or in the directory `UME_KEYCLOAK_ADDITIONS_DIR` names): Keycloak realm
representations with `"realm": "ume"` and only that system's roles, clients, scope mappings and identity providers.
`realm-import` applies them after `../realm/*.json`, in name order, on every `docker compose ... up`. The values of
their `$(env:NAME)` placeholders go in the file `UME_KEYCLOAK_IMPORT_ENV` names. See docs/keycloak.md, *Realm files*.

Files here are not part of this repository (`.gitignore`): each system delivers its own.
