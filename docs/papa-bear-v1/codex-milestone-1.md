# Codex task — Milestone 1: stabilize the OpenAI tool loop

## Objective

Repair the existing v0.3 text assistant so direct Responses API answers and multi-round local function calls both complete reliably. Improve diagnostics without changing the Arma PBO, native DLL or current Named Pipe protocol.

## Reproduction

Environment: current `main`, model `gpt-5-mini`, Arma connected.

Direct question succeeds:

```text
Welche Karte ist geladen und in welche Richtung schaue ich?
```

A question that causes a tool call fails and logs only:

```text
OpenAI assistant failed: InvalidOperationException.
```

Observed example:

```text
Ist hier eine Ortschaft in der Nähe?
```

## Required investigation

1. Reproduce with a mocked Responses API sequence before relying on a live key.
2. Inspect `OpenAiAssistantService` and verify the complete request/response sequence for a reasoning model with `store: false`.
3. Consult the current official OpenAI Responses API documentation for manually managed context and function-call output.
4. The current implementation appends response output items but does not request `reasoning.encrypted_content`. Verify whether stateless continuation requires `include: ["reasoning.encrypted_content"]` and preservation of all required reasoning items. Implement the documented pattern rather than a guessed workaround.
5. Verify `function_call`, `call_id`, JSON arguments, `function_call_output` and final message parsing.

## Implementation requirements

- Keep `store: false`.
- Continue to use strict typed tools and local validation.
- Preserve all response items required by the API between tool rounds.
- Support at least three sequential tool rounds without losing context.
- Do not use `previous_response_id` if it conflicts with the chosen privacy/stateless design; document the selected continuation strategy.
- Surface a concise actionable error in the Assistant UI.
- Log HTTP status, OpenAI error type/code/message and failing stage, but never API keys, authorization headers, full prompts, telemetry, tool results or conversation text.
- Preserve cancellation and timeouts.
- Do not add location indexing, ACE, persona, voice or action features in this milestone.
- Do not change SQF, C++ or bridge schemas unless a demonstrated defect requires it; any such need must be reported before implementation.

## Tests

Add deterministic tests around a replaceable/mock HTTP transport for:

1. direct answer with zero tools;
2. one `query_environment` call followed by a final answer;
3. two sequential tool calls followed by a final answer;
4. malformed tool arguments returned to the model as a safe tool error;
5. Arma query timeout returned to the model as a safe tool error;
6. OpenAI 4xx error parsing;
7. cancellation during API request and during Arma query;
8. no sensitive content in logs.

## Manual acceptance

With Arma connected:

- direct map/heading question completes with zero tool calls;
- “Gibt es Gebäude in meiner Nähe?” completes with at least one tool call and a grounded answer;
- “Ist hier eine Ortschaft in der Nähe?” must not crash. Until Milestone 4 provides named-location knowledge, the assistant must either state that it cannot confirm a named settlement or give a clearly qualified building-based observation;
- ten mixed direct/tool questions complete without UI freeze or pipe disconnect;
- the UI displays the actual diagnostic cause for an intentionally invalid key/model.

## Delivery

- focused branch and pull request;
- green Windows CI;
- summary of root cause and chosen Responses continuation pattern;
- automated test results;
- manual Arma test instructions;
- no unrelated refactoring.

Suggested Codex instruction:

```text
Read AGENTS.md and all files under docs/papa-bear-v1/. Implement only docs/papa-bear-v1/codex-milestone-1.md. First reproduce and explain the root cause, then implement tests and the minimal fix. Do not modify the Arma addon or native DLL. Build and test on Windows, prepare a focused PR, and report any requirement that cannot be verified from official documentation.
```
