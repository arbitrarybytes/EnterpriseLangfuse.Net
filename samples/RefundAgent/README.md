# RefundAgent sample

A runnable tour of EnterpriseLangfuse.NET. **No configuration required.**

```bash
dotnet run --project samples/RefundAgent
```

## What you get with zero setup

Without credentials the sample points at `https://langfuse.invalid` — a host RFC 2606 guarantees can
never resolve. That is not a crippled demo; it is scenario 2. The application keeps serving prompts
through a total Langfuse outage, which is the framework's headline claim, demonstrated rather than
described:

```
── 1. Typed prompt → traced LLM call ─────────────────────────────────────────
  prompt   : RefundAgent v0  (source: EmbeddedFallback)
  rendered : Customer Ada Lovelace is asking about order A-4815: My order arrived damaged…
  model    : I'm sorry your order arrived damaged. Order A-4815 is within the 30-day window…
  tokens   : 115 total

── 2. Zero-downtime: the same call while Langfuse is unreachable ─────────────
  source   : EmbeddedFallback
  ✔ Langfuse is unreachable and the application is still serving prompts.
    Source reports EmbeddedFallback, so you can alarm on running degraded.

── 3. Telemetry never blocks the caller ──────────────────────────────────────
  recorded 1,000 generations in 3.7 ms (3.7 µs each) — no network on this path
```

Two details worth noticing in that output:

- **`v0`** — an embedded fallback has no server-assigned version, so `Version` is 0. Check `Source`
  before treating a version as authoritative.
- **The `fail:` lines after "Done"** are the design working. Telemetry could not be delivered, so the
  dispatcher logged it and dropped the batch. It never threw, never blocked the caller, and never
  faulted the host.

## Running against a real Langfuse

Get a free key pair from [cloud.langfuse.com](https://cloud.langfuse.com) (Settings → API Keys), then
pick either:

```bash
# Environment variables
export LANGFUSE_PUBLIC_KEY=pk-lf-...
export LANGFUSE_SECRET_KEY=sk-lf-...
export LANGFUSE_BASE_URL=https://cloud.langfuse.com    # optional; self-hosted goes here

# ...or a gitignored file at the repo root (langfuse.local.env)
LANGFUSE_PUBLIC_KEY=pk-lf-...
LANGFUSE_SECRET_KEY=sk-lf-...

# ...or user secrets, which keep keys out of the working tree entirely
dotnet user-secrets set LANGFUSE_PUBLIC_KEY pk-lf-... --project samples/RefundAgent
```

Now scenario 1 resolves live from Langfuse, and the traces show up in your project with the prompt
link and token usage attached.

To see the fallback again with credentials configured, block the host (or pull your network) and
re-run — the same code path serves the embedded copy.

## What the code demonstrates

| File | Shows |
| :--- | :--- |
| [`Prompts/RefundAgent.prompt.yaml`](Prompts/RefundAgent.prompt.yaml) | The prompt as version-controlled source. |
| [`RefundAgent.csproj`](RefundAgent.csproj) | Why each prompt is registered **twice** — `AdditionalFiles` drives codegen, `EmbeddedResource` is the offline fallback. |
| [`Program.cs`](Program.cs) | `GetRefundAgentPromptAsync(customerName, orderId, question)` — generated; its parameters *are* the `{{variables}}`. Delete one and it stops compiling. |
| [`CannedChatClient.cs`](CannedChatClient.cs) | Why no LLM key is needed, and how to swap in a real provider. |
| [`LangfuseCredentials.cs`](LangfuseCredentials.cs) | Credential discovery, and how to keep secrets out of logs. |

## Using a real model

Replace one line in `Program.cs`:

```diff
- builder.Services.AddChatClient(new CannedChatClient())
+ builder.Services.AddChatClient(new AnthropicChatClient(apiKey))
                  .UseLangfuse(...);
```

Nothing else changes. `UseLangfuse()` traces whatever `IChatClient` it wraps without knowing which
provider it is.
