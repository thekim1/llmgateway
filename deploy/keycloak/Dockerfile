# Keycloak built for production: `kc.sh build` fixes the build-time options in the image, so the container starts
# with `start --optimized` and runs with a read-only file system. See docs/keycloak.md.
# Pinned by digest; update only to a build that is at least 7 days old (docs/upgrade-guide.md, supply chain).
FROM quay.io/keycloak/keycloak:26.8.1-1@sha256:a78d6b79572168cb25886b80a8b5b8d4ed4390743e3eecb810538683e209dd6b

# One instance: local caches, no cluster discovery. Health and metrics on the management port (9000), never published.
ENV KC_DB=postgres \
    KC_CACHE=local \
    KC_HEALTH_ENABLED=true \
    KC_METRICS_ENABLED=true

RUN /opt/keycloak/bin/kc.sh build
