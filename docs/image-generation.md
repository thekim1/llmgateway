# Image generation

Image models (gpt-image-1, DALL·E 3, FLUX and others with an OpenAI-compatible API) are served at
`POST /v1/images/generations` (a JSON body) and `POST /v1/images/edits` (a `multipart/form-data` upload of one or more
pictures in `image` or `image[]`, and an optional `mask`). Clients use the OpenAI SDKs unchanged:

```bash
curl https://<gateway>/v1/images/generations -H "Authorization: Bearer $UME_API_KEY" \
  -H "Content-Type: application/json" -d '{"model":"ume/image","prompt":"En röd stuga vid en sjö","size":"1024x1024"}'
curl https://<gateway>/v1/images/edits -H "Authorization: Bearer $UME_API_KEY" \
  -F model=ume/image -F prompt="Gör himlen blå" -F image=@stuga.png
```

**Setting it up.** OpenAI, Azure OpenAI and Foundry providers serve images; an OpenAI-compatible server needs *Image
generation and editing* under *Endpoints this server offers*. Add the model with kind
*Image generation* (`Image`; discovery does this for `gpt-image*`, `dall-e*`, `flux*`, `imagen*` and `stable-diffusion*`
models) and put it behind an alias with the same kind. Fallback, residency, provider allow-lists, rate limits, budgets
and routing rules (`endpoint == "image_generations"` or `"image_edits"`) work as for chat. The dev seed has `ume/image`
on FakeLlm.

**Prices.** DALL·E-style models are priced per picture (*Image (USD per picture)* in the price form, `perImageUsd`);
gpt-image-1 per token (the usual input and output prices; the answer's `usage` is read). The pictures are counted from
the answer's `data` array, so a request for `n=4` that returns three is billed for three. A per-picture price is one
flat amount for every size and quality, so it is an estimate for models that price by them; token pricing (gpt-image-1) is exact.
Image input tokens of an edit are billed at the text input price. Migration `ImageGeneration` adds `PerImageUsd`.
Before the call, the budget reserves the most the request could cost (`n` pictures, up to 6 400 output tokens each).

**Limits and privacy.** Edits may upload 50 MB in all (`Gateway__MaxImageRequestBodyBytes`, at most 16 pictures; raise
the proxy's `client_max_body_size` with it). Pictures are held in memory for the duration of the request only, never
written to disk or logged, and sent again from memory on fallback. A key whose **Attached files** setting is *Text only*
cannot edit pictures (`attachment_not_allowed`); *Images only* may. The PII policy scans the `prompt` (and the other
form fields) but cannot see inside pictures, and generated pictures are returned as the provider made them. Streaming
(`stream`, `partial_images`) is not supported and answers `400 invalid_request`. Pictures a provider returns as a URL
(DALL·E) point at the provider's storage; ask for `response_format: "b64_json"` to keep them off third-party links.
