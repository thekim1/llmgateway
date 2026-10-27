# Data retention and request logging

Request data is removed automatically so the database does not keep growing. Under **Settings > Data retention** a
gateway admin sets two periods (90–3 650 days, both 365 by default):

- **Usage data:** usage records, refused-key records and old budget alerts. Whole months go, and each month's totals
  per key, model and provider are saved first, so costs, budgets (yearly ones too), the usage summary, the CSV export and the
  Data API monthly figures stay complete. The request list, PII incidents and Data API feeds no longer show removed months.
- **Audit log:** a separate period, or kept for ever.

The admin API runs the removal every night at 03:00 (and on *Run now*) through a database function, so it never needs
delete rights on those tables itself. Set both periods from your records-retention decision (gallringsbeslut) before going
live: removed rows cannot be restored except from a backup. Details: *Data retention* in the [runbook](runbook.md).

## Request logging

Every request made with a valid key is recorded as a usage record (time, key, team, department, model, provider, tokens,
cost, latency, status, outcome, PII categories and routing rule), whatever the log level: request counts, outcomes and
latency are on **Monitor > Usage**, in the Data API and in the `ume.gateway.requests` metric. Requests refused before a key is
recognised are counted as refused credentials. Message content is never logged or stored.

The gateway log only has a line for refused, failed and cancelled requests: writing one for every request costs a noticeable
share of peak throughput ([performance](performance.md)). To follow successful requests in the log too, a gateway admin turns on
**Settings > Request logging**, for 24 hours (it then switches itself off) or without a time limit. It reaches every gateway
instance within seconds, without a restart, and is audited.

Outside development the services write their console log as JSON (one object per line, with the message and its fields),
which log shippers read directly and which cannot be broken up by line breaks in client-supplied values. For the
plain-text format of earlier versions set `Logging__Console__FormatterName=simple`.
