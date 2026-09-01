# JARVIS 0.1.1 manual wake-word test matrix

This matrix requires a physical Windows microphone and the installed local model assets. Automated tests prove lifecycle behavior but do **not** prove keyword accuracy, acoustic range, false-positive rate, speaker feedback behavior, or idle CPU/battery use. Do not commit recordings, transcripts, device names, machine identifiers, or result logs.

## Fixed preconditions and provisional tuning

1. Run `./scripts/setup-local-ai.ps1 -DownloadRuntime -DownloadModels` and `./scripts/diagnose-local-ai.ps1`.
2. Set `JARVIS_Voice__Enabled=true`, `JARVIS_Voice__SpeechOutputEnabled=true`, and `JARVIS_Voice__WakeWord__AlwaysListeningEnabled=true`.
3. Begin with keyword score `1.5`, threshold `0.25`, cooldown `3.0 s`, continuation `30.0 s`, maximum active paths `4`, trailing blanks `2`, one KWS CPU thread, and the phrase “Jarvis”. These values require physical tuning.
4. Start JARVIS and confirm `[voice: Sleeping]` plus `[capture: WakeWord]`. Do not enter `/start` unless that row asks for it.
5. Inspect the interactive console and numeric structured metric events. The relevant metrics are keyword detection latency, false activation count, wake-to-listening latency, warm LLM first-token latency, and TTS first-audio latency. No metric should contain microphone content.

For every positive row, failure means no activation, duplicate activation, host fault/exit, capture ownership error, or an unexpected second required wake phrase. For every negative row, an activation is a false positive: enter `/falsewake` immediately so the content-free count is recorded and the listener is rearmed.

## Acoustic matrix

| Scenario | Exact action | Expected behavior | Additional failure signal |
| --- | --- | --- | --- |
| Quiet room | At about 1 m, say “Jarvis” naturally five times, allowing sleep between trials. | Exactly one activation per trial; short acknowledgement; then `Listening`. | Miss, duplicate, or wide latency variance. |
| Normal room noise | Repeat five trials with ordinary fan/HVAC/typing noise. | Same state progression without materially increased false activations. | Background noise activates JARVIS or most phrases are missed. |
| Laptop speakers | In VAD mode, let JARVIS speak, allow it to sleep, then play ordinary spoken content from the laptop speakers for five minutes; include content that says “Jarvis” separately. | Ordinary content does not activate; an intentional audible “Jarvis” is recorded as speaker-origin behavior, not claimed as user discrimination. | Repeated self/ordinary-content activation or capture fault. |
| Headphones | Wear headphones and repeat quiet-room, response, follow-up, and barge-in trials. | Wake, audio response, continuation, and interruption remain reliable; no self-trigger from assistant output. | Missed barge-in, stale playback, or device lock. |
| Near microphone | At 10–20 cm, say “Jarvis” softly, normally, and loudly, five trials each. | One activation per utterance without clipping-related duplicate activation. | Soft misses, loud duplicates, or native/audio fault. |
| 0.5 m | Say “Jarvis” naturally five times. | Record detections and latency; do not generalize beyond this device. | Any miss/duplicate is recorded for tuning. |
| 1 m | Say “Jarvis” naturally five times. | Record detections and latency. | Any miss/duplicate is recorded for tuning. |
| 2 m | Say “Jarvis” naturally five times. | Record detections and latency. | Any miss/duplicate is recorded for tuning. |
| 3 m | Say “Jarvis” naturally five times. | Record detections and latency; this is exploratory, not a required supported range. | Any miss/duplicate is recorded for tuning. |
| Similar-sounding words | Say “service”, “jars”, “justice”, “Jarvie”, “Jervis”, “gorgeous”, and “Java” five times each without saying “Jarvis”. | Remains `Sleeping`; no acknowledgement or LLM startup. | Any activation is labeled with `/falsewake`. Do not log the microphone recording. |
| Repeated “Jarvis” | Say “Jarvis, Jarvis, Jarvis” rapidly, then repeat after cooldown. | First cluster produces one accepted activation; cooldown prevents a duplicate. A later utterance can activate after sleep/cooldown. | Multiple simultaneous sessions, repeated acknowledgement, or microphone conflict. |

## Lifecycle, fallback, privacy, and resource checks

| Scenario | Exact action | Expected behavior | Failure signal / inspection |
| --- | --- | --- | --- |
| Continuation | Wake JARVIS, ask one question, then ask two follow-ups less than 30 seconds apart without “Jarvis”. | Both follow-ups are accepted; each activity refreshes the window. | A second phrase is required or sleep occurs during active speech/generation. Inspect state/capture events. |
| Idle return | Finish a turn and remain silent for more than 30 seconds. | Conversation capture stops, state becomes `Sleeping`, wake capture rearms, and history is cleared. | Stays conversational indefinitely, device conflict, or wake no longer works. |
| Always-listening disabled | Restart with `AlwaysListeningEnabled=false` and wait two minutes while saying “Jarvis”. | No wake capture and no activation. | Microphone opens for wake detection or LLM starts. |
| Manual diagnostics | With wake enabled and sleeping, enter `/start`. | Dormant listener stops cleanly and a normal conversation starts without the phrase. | Double capture, already-active exception, or host crash. |
| Push-to-talk fallback | While sleeping, enter `/ptt`, speak, then `/send`. | A push-to-talk session starts without the phrase; capture exists only between commands. | Wake phrase required, capture leaks past `/send`, or duplicate turn. |
| Barge-in | With headphones, wake JARVIS, request a long response, and speak during output. | Playback stops promptly; new speech becomes the current turn; stale audio never resumes. | Audible stale continuation or stuck state. Inspect barge-in metric. |
| Initial lazy load | Start fresh, leave sleeping for two minutes, and observe processes/memory before the first wake. | No `llama-server` process before the first wake; only host, microphone, and KWS are active. | LLM process/model starts before wake. |
| Idle resource use | After warm-up, restart fresh and measure host CPU/working set for ten sleeping minutes; repeat on battery if safe. | CPU remains low and stable with no unbounded memory growth. Record approximate aggregate values only. | Sustained busy core, growing memory, repeated model load, or battery drain beyond an agreed target. No numeric target is claimed yet. |
| Offline/privacy | Disconnect all external networking, complete wake and follow-up trials, then inspect active connections and `JARVIS_HOME` Logs/Data/Cache. | Wake and conversation work locally; only loopback LLM traffic exists; no raw audio or transcript artifact exists. | External connection, raw PCM/audio/transcript file, or content-bearing structured metric. |
| Clean shutdown | Press Ctrl+C while sleeping; repeat during activation and conversation. | Host exits promptly, capture releases, and managed llama process (if started) terminates. | Hang, orphan process, stack trace, or locked microphone. |

## Tuning procedure

Change one value at a time and repeat the entire relevant positive and negative rows. Increasing `KeywordThreshold` generally demands stronger evidence; changing `KeywordScore` changes the keyword-path score. Do not infer the best direction from a single trial. Preserve the lowest false-positive configuration that still meets the agreed detection rate across the tested noise/distance conditions, then repeat on each supported microphone class.

Record only aggregate trial counts and approximate timing/resource statistics outside the repository. Keyword accuracy remains **REQUIRES USER MANUAL VERIFICATION** until this matrix has been run on target hardware. The repository defaults remain provisional regardless of automated-test success.
