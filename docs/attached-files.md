# Attached files

Applications send images, PDFs, audio and video inline in the request body, as content parts
(`image_url`/`file`/`input_audio` in Chat Completions, `input_image`/`input_file` in Responses, `image`/`document` in
Anthropic Messages, and `video_url` for video models on vLLM and other OpenAI-compatible servers). The gateway has
no upload endpoint: a provider `file_id` only works with the one provider account it was uploaded to, which would
break alias routing and fallback.

Bodies are limited to 16 MB (`Gateway__MaxRequestBodyBytes`, at most 100 MB); larger requests get 413
`request_too_large`. Base64 adds a third, so this fits about 12 MB of files, enough for photos and PDFs but only short
video clips. Send longer video as a URL the model server can fetch, or raise the limit. Whether a model accepts a given
kind of file is up to the provider; Claude models take images, PDFs and plain text but not audio or video.

Budget reservations count each image as 1 600 input tokens instead of by its base64 size (a 4 MB photo used to
reserve about a million tokens and could hit 402 `budget_exceeded` on a small budget). Documents, audio and video are
still estimated from their size, which over-reserves; the actual cost is settled from the provider's reported usage.

Each key has an **Attached files** setting (Keys > edit key > Access):

| Setting | Effect |
|---|---|
| **All files** (`Allowed`, default) | Everything is sent on, as before. |
| **Images only** (`ImagesOnly`) | Images are sent on; documents, audio, video and `file_id` references are rejected. |
| **Text only** (`None`) | Any file is rejected. |

A refused request gets 400 `attachment_not_allowed`. The PII policy reads text, not file contents, so use **Text only**
for keys whose users handle data that must never reach a provider. A chat front-end that extracts a file's text itself
and sends it as an ordinary message is not stopped by this setting; the PII policy covers that text. Details:
[admin-api.md](admin-api.md#gateway-data-plane--for-the-developer-portal-snippets).

Chat Completions requests to Claude models now carry PDF and plain-text files as Anthropic `document` blocks (they used
to be dropped silently). Parts Claude cannot take (audio, video, other file types, OpenAI `file_id`) return 400
`unsupported_content` instead of being left out of the prompt.
