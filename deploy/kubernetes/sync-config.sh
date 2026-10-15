#!/bin/sh
# Copies the configuration files the Kubernetes manifests need from deploy/postgres and deploy/redis: Kustomize only
# reads files inside its own directory. Run after changing those files; CI fails when the copies are stale.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
cp "$here/../postgres/app-roles.sql" "$here/base/app-roles.sql"
cp "$here/../postgres/init.sql" "$here/../postgres/pg_hba.conf" "$here/components/datastores/"
cp "$here/../redis/redis.conf" "$here/components/datastores/"
