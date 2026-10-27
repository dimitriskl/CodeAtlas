import { CommandError } from "./command-error.js";

const instructions = 'Return only a JSON object: {"ok":true}.';
const connectionPrompt = "Connection test. Do not use tools or obtain any external context.";
const providerFailed =
  "AI generation or connection test failed or returned an incomplete or invalid response. Check provider configuration, model and effort.";
const maximumProviderBytes = 262_144;

export type AiConnection = {
  provider: string;
  model: string;
  endpoint: string;
  effort: string | null;
  apiKey: string | null;
};

/** Sends the fixed connection-test prompt. The request carries no project source. */
export async function testAiConnection(
  configuration: AiConnection,
  fetchImpl: typeof fetch = globalThis.fetch,
): Promise<void> {
  const payload = JSON.stringify(requestBody(configuration));
  const headers = new Headers({ "content-type": "application/json" });
  if (configuration.provider === "anthropic") {
    headers.set("x-api-key", configuration.apiKey ?? "");
    headers.set("anthropic-version", "2023-06-01");
  } else if (configuration.apiKey !== null) {
    headers.set("authorization", `Bearer ${configuration.apiKey}`);
  }
  let response: Response;
  try {
    response = await fetchImpl(configuration.endpoint, {
      method: "POST",
      headers,
      body: payload,
      redirect: "manual",
      signal: AbortSignal.timeout(120_000),
    });
  } catch {
    throw new CommandError("ai_provider_failed", providerFailed, 1);
  }
  if (response.status < 200 || response.status >= 300) {
    await response.body?.cancel();
    throw new CommandError(
      "ai_provider_failed",
      `AI request failed (HTTP ${response.status}). Check provider, model, effort, endpoint and key; response body omitted.`,
      1,
    );
  }
  const length = response.headers.get("content-length");
  if (length !== null && Number(length) > maximumProviderBytes) {
    await response.body?.cancel();
    throw new CommandError("ai_provider_failed", providerFailed, 1);
  }
  const text = await response.text();
  if (Buffer.byteLength(text, "utf8") > maximumProviderBytes) {
    throw new CommandError("ai_provider_failed", providerFailed, 1);
  }
  try {
    const content = generatedText(configuration.provider, JSON.parse(text) as unknown);
    const parsed = JSON.parse(content) as unknown;
    if (!isJsonObject(parsed) || parsed.ok !== true) {
      throw new CommandError("ai_provider_failed", providerFailed, 1);
    }
  } catch (error) {
    if (error instanceof CommandError) throw error;
    throw new CommandError("ai_provider_failed", providerFailed, 1);
  }
}

function requestBody(configuration: AiConnection): Record<string, unknown> {
  if (configuration.provider === "anthropic") {
    const body: Record<string, unknown> = {
      model: configuration.model,
      system: instructions,
      messages: [{ role: "user", content: connectionPrompt }],
      max_tokens: 2048,
    };
    if (configuration.effort !== null) body.output_config = { effort: configuration.effort };
    return body;
  }
  const body: Record<string, unknown> = {
    model: configuration.model,
    messages: [
      { role: "system", content: instructions },
      { role: "user", content: connectionPrompt },
    ],
    response_format: { type: "json_object" },
  };
  const budget =
    configuration.provider === "openai" || configuration.provider === "azure"
      ? "max_completion_tokens"
      : "max_tokens";
  body[budget] = 2048;
  if (configuration.provider === "openai" || configuration.provider === "azure") body.store = false;
  if (configuration.effort !== null) {
    if (configuration.provider === "kimi" && configuration.effort === "none") {
      body.thinking = { type: "disabled" };
    } else {
      body.reasoning_effort = configuration.effort;
    }
  }
  return body;
}

function generatedText(provider: string, root: unknown): string {
  if (!isJsonObject(root)) throw new Error("invalid");
  if (provider === "anthropic") {
    if (root.stop_reason !== "end_turn") throw new Error("incomplete");
    const blocks = root.content;
    if (!Array.isArray(blocks)) throw new Error("invalid");
    const texts: string[] = [];
    for (const block of blocks) {
      if (!isJsonObject(block)) throw new Error("invalid");
      if (block.type === "text" && typeof block.text === "string") texts.push(block.text);
      else if (block.type !== "thinking" && block.type !== "redacted_thinking")
        throw new Error("invalid");
    }
    return texts.join("");
  }
  const choices = root.choices;
  if (!Array.isArray(choices) || choices.length !== 1) throw new Error("invalid");
  const choice = choices[0];
  if (!isJsonObject(choice) || choice.finish_reason !== "stop") throw new Error("incomplete");
  const message = choice.message;
  if (!isJsonObject(message) || typeof message.content !== "string") throw new Error("invalid");
  if ("refusal" in message && message.refusal !== null && message.refusal !== undefined) {
    throw new Error("refusal");
  }
  return message.content;
}

function isJsonObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
