# Restricting a key to client addresses

A key can be limited to the addresses it may be used from, so a leaked key does not work elsewhere. In the admin UI,
edit the key (*Keys*) and enter **Allowed client addresses**: one IP address (`192.0.2.10`, `2001:db8::10`) or CIDR
network (`203.0.113.0/24`) per line, at most 100. Empty means any address. A request from anywhere else is refused with
`403 address_not_allowed` and recorded with its source address like other refused keys. The list is copied to the new key
on rotation.

**Behind a reverse proxy** (the usual production setup), every request reaches the gateway from the proxy. A gateway admin
must list the proxy under **Settings > Trusted proxies**; the gateway then takes the client address from
`X-Forwarded-For`, but only on connections from those addresses. Until then, keys with an address restriction are refused.
Caveats:

- Trust only proxies you operate, never a network that also holds clients: a trusted address can claim to be anyone.
  `0.0.0.0/0` is refused.
- The proxy must append the client address (`proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`; Nginx Proxy
  Manager does it by default). With several proxies in a row, list them all.
- The Settings page shows the address your own request to the admin API came from, which is usually the proxy.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED` / `UME_FORWARDED_HEADERS` no longer affect the gateway: they would believe the
  header from anyone.
- An address restriction narrows where a key works; it does not replace the key or the firewall.

Both checks run in memory (no database or Redis call): well under a microsecond per request, and nothing at all when
unused (see [performance](performance.md)). Details and troubleshooting: *Client addresses and trusted proxies* in the
[runbook](runbook.md).
