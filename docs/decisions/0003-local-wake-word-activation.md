# 0003 — Local open-vocabulary wake activation and continuation lifecycle

- Status: Accepted
- Date: 2026-08-31
- Owners: team
- Supersedes: none

## Context

JARVIS 0.1 required `/start` or push-to-talk before conversation components were useful. Version 0.1.1 needs a local “Jarvis” wake phrase, low dormant CPU use, no cloud service or audio persistence, and a continuation window that does not require the phrase before every follow-up. The accepted local inference architecture in ADR 0002 must remain intact.

## Decision

Keep `IWakeWordDetector` in Core as the replaceable boundary and implement it in Infrastructure with sherpa-onnx keyword spotting. Pin `sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01`, using its int8 transducer files, one CPU inference thread, and the BPE token sequence `▁JA R VI S @JARVIS` for the supported phrase.

Core owns the lifecycle `Sleeping → Activating → Listening/Conversation → Sleeping`, duplicate cooldown, continuation timeout, cancellation, and content-free metrics. While initially sleeping, only the microphone and keyword model are active. The LLM, VAD, ASR, and TTS initialize after an accepted wake. They may remain warm after the first activation until host shutdown; conversation history is cleared on return to sleep.

Always-listening is opt-in. `/start` and push-to-talk remain independent diagnostic/fallback activation paths. No dormant audio is persisted or logged.

Initial, unvalidated acoustic values are score `1.5`, threshold `0.25`, cooldown `3 seconds`, continuation `30 seconds`, maximum active paths `4`, and trailing blanks `2`. These are starting points, not accuracy claims.

## Alternatives considered

- A fixed-phrase wake engine was not selected because the accepted speech runtime already exposes open-vocabulary keyword spotting and replaceability is a requirement.
- Cloud keyword detection was rejected because it violates offline and privacy requirements.
- Loading the entire conversation stack at startup was rejected because it wastes dormant resources and unnecessarily initializes the LLM.
- Unloading the LLM after every continuation timeout was deferred because it would increase subsequent wake latency and require a broader language-model lifecycle contract.

## Consequences

- Initial sleep has a small dedicated native model and one inference thread, but real idle CPU and battery behavior still require measurement.
- The current pinned model and tokenization support only the configured English phrase “Jarvis”; a future model/profile can replace the adapter without changing Core.
- Speaker feedback and acoustic false positives remain possible without echo cancellation. Headphones are the baseline for reliable barge-in and wake testing.
- A user can report a false activation with `/falsewake`; this records only a cumulative number.

## Validation

Automated tests cover lazy initialization, state transitions, duplicate cooldown, continuation refresh/expiry, cancellation, capture release, and push-to-talk coexistence. The [manual wake-word matrix](../testing/manual-wake-word-test-matrix.md) is mandatory before reporting keyword accuracy, distance, false-positive rate, speaker behavior, or idle-resource results.
