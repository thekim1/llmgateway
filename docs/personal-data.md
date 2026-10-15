# Personal data (PII)

Each key has a **PII policy**; the default is **Off**, which means the key's requests are **not scanned at all**. To test
detection, set the key's policy (*Keys*, edit the key) to one of:

| Policy | What happens when the gateway finds personal data in the text |
|---|---|
| Off | Nothing; no scanning. |
| Allow | Detected and recorded (category and count), sent on unchanged. Response header `x-ume-pii: detected`. |
| Redact | Values are replaced with `[PERSONNUMMER]`, `[SAMORDNINGSNUMMER]`, `[E-POST]`, `[TELEFON]` or `[IBAN]` before the request leaves the gateway. |
| Block | The request is refused with `pii_blocked`. |
| On-prem (`RerouteToOnPrem`) | The request may only go to on-prem models. |

What is detected, in all text a client sends (chat messages as a string or as content parts, Responses input, Anthropic
messages, the transcription `prompt`, realtime text events; not file contents):

- **Personnummer and samordningsnummer** (day + 60) with a real calendar date, as `YYMMDD-NNNN`, `YYMMDD+NNNN`, `YYMMDD NNNN`,
  `YYYYMMDD-NNNN`, `YYYYMMDD NNNN` or `YYYYMMDDNNNN` (century 18, 19 or 20). These are caught even when the check digit is
  wrong, because people paste made-up and mistyped numbers. A bare 10-digit number (`YYMMDDNNNN`) must also have a valid
  check digit (Luhn): without it, a mobile number such as `0701234567` would look like a personnummer.
- **Email addresses, Swedish phone numbers and IBANs** (mod-97 checked).

Detection is a safety net, not a guarantee that no personal data reaches a provider. The values themselves are never
stored or logged, only the categories and counts (`Personnummer:2,Email:1`).

**PII incidents** (admin UI, *Monitor > PII incidents*; `GET /api/usage/pii-incidents`) lists every request in which
personal data was found, newest first: when, which key, team and department, endpoint and model, what was found and what
the gateway did (detected and sent on, redacted, blocked, sent on-prem). Filter by period, department, team, key, category
and action; tiles show the number of requests per category and per action for the period. Each row links to the request on
the *Usage* page. Department admins and viewers see their own departments. A key with policy Off never has incidents.
