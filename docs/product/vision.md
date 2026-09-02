# JARVIS product vision

JARVIS is a local personal computing agent for Windows. It should feel immediate in conversation, understand the user's projects, and eventually perform useful computer actions without turning a probabilistic model into an operating-system principal.

## Product promise

JARVIS should provide:

- natural, interruptible voice and text conversation;
- useful operation without a subscription, API key, or Internet connection;
- local processing and minimum-context retrieval by default;
- grounded technical explanations tied to files and symbols;
- explicit approval for sensitive actions and an auditable tool history;
- replaceable inference, speech, Windows, storage, and UI implementations;
- graceful degradation when a model, device, or accelerator is unavailable.

The product is not a kernel component, remote-control backdoor, autonomous administrator, fictional all-powerful assistant, or a wrapper that gives an AI model unrestricted shell/filesystem access. Codex helps develop JARVIS and is not part of its runtime.

## Trust commitments

Models receive only the input required for the current turn. They never directly open files, invoke processes, control applications, or change the system. Those future capabilities must be expressed as strongly typed tools, pass authorization, and produce an audit record. Security-sensitive actions default to denial or explicit approval.

Normal runtime is offline-capable after explicit user-controlled setup. JARVIS does not upload microphone audio, transcripts, prompts, repositories, screenshots, tool results, or telemetry. Native models and runtime data stay outside Git in the user's JARVIS application-data root.

## Experience goals

Voice interaction should have low perceived latency: speech start/end is detected locally, recognition is streamed, language output is segmented into speakable units, and TTS begins before a long response completes. Speaking over JARVIS cancels the previous generation and immediately clears stale playback. Text and push-to-talk remain dependable fallbacks.

The persona is calm, concise, professional, technically precise, and honest about capability. It does not reveal hidden reasoning or claim that an action completed without a confirmed tool result.

## Current 0.2 scope

Version 0.2 retains the local voice vertical slice—supervised loopback llama.cpp/Qwen3 inference, sherpa-onnx wake-word/VAD/ASR/TTS, Windows audio, streaming segmentation, barge-in, push-to-talk, and text debugging—and adds the first permission-controlled typed tool kernel. Its bounded catalog supports approved local reads and a few visible local actions; it does not provide writes, deletion, arbitrary shell, elevation, credential access, project intelligence, persistent memory, GUI automation, or IoT. See [the roadmap](roadmap.md) for staged scope.
