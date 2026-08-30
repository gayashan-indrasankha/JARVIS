# Manual realtime voice smoke test

Automated tests use fake transports and audio devices. They do not prove that a particular microphone, speaker, network, OpenAI account, acoustic environment, or Windows driver works. Run this procedure on every machine/device combination intended for use.

Do not record or share the console output: assistant transcript text is visible there. Structured logs are JSON on standard output and intentionally exclude transcripts and raw audio.

## 1. Configuration and connection

1. **Preconditions:** Windows 10/11; .NET 10 SDK; working default input/output devices; an OpenAI API key with Realtime API access stored as described in `README.md`; `Voice:Enabled=true`; default server-VAD mode.
2. **Action:** run `dotnet run --project src/Jarvis.Host/Jarvis.Host.csproj`, then enter `/start`.
3. **Expected:** state progresses through `Activating` to `Listening`; a structured `OpenAI realtime session connected` event appears; no credential is printed.
4. **Failure:** the process exits, state becomes `Faulted`, connection never reaches `Listening`, or any API-key fragment appears.
5. **Inspect:** console JSON events 2100–2102 and sanitized `[voice error: ...]` output. Confirm user-secret keys with `dotnet user-secrets list --project src/Jarvis.Host/Jarvis.Host.csproj` without copying values into an issue.

## 2. Natural speech and streamed playback

1. **Preconditions:** test 1 passed and the session is `Listening`.
2. **Action:** say a short question, then remain quiet.
3. **Expected:** JARVIS detects the turn, changes through `AwaitingResponse`/`Speaking`, begins playing audio before the whole response is complete, displays transcript deltas, and returns to `Listening`.
4. **Failure:** no turn is detected, output arrives only after a long unexplained delay, audio is distorted/at the wrong speed, or the host crashes.
5. **Inspect:** audio lifecycle events 2200–2202, state lines, provider reconnect/error codes, Windows microphone privacy settings, and the system's default recording/playback devices.

## 3. Barge-in

1. **Preconditions:** use speakers first, then repeat with a headset; server-VAD session is active.
2. **Action:** ask for a response long enough to speak for several seconds. While JARVIS is audibly speaking, begin a new question.
3. **Expected:** speaker output stops promptly, no buffered tail continues, state shows `Interrupted`, and the new utterance produces a new response.
4. **Failure:** old audio continues noticeably, old and new responses overlap, late chunks from the interrupted item resume, or the new speech is ignored.
5. **Inspect:** state/error lines. Note device type, approximate speech-to-stop latency, room echo, volume, and whether the failure reproduces with a headset; raw audio is intentionally unavailable in logs.

## 4. Push-to-talk fallback

1. **Preconditions:** set `Voice:ActivationMode=PushToTalk`, restart JARVIS, and `/start`.
2. **Action:** enter `/ptt`, speak one question, then enter `/send`.
3. **Expected:** capture begins only after `/ptt`, `/send` commits the turn, and streamed spoken output follows. Entering `/interrupt` while it speaks stops output.
4. **Failure:** microphone capture starts before `/ptt`, `/send` produces no response, or `/interrupt` leaves audio playing.
5. **Inspect:** state lines plus audio lifecycle events 2200–2204.

## 5. Text-console fallback

1. **Preconditions:** any configured active voice session; microphone input may be unavailable.
2. **Action:** type `Say the word ready.` as a plain console line.
3. **Expected:** the line is submitted as a text turn, spoken output streams to the speaker, and transcript text is displayed.
4. **Failure:** the line is treated as a command, no output arrives, or the process terminates.
5. **Inspect:** sanitized command/provider error class and reconnect state. Do not paste sensitive text into this debug path.

## 6. Network disconnect and recovery

1. **Preconditions:** active session; a safe way to disconnect/reconnect the test machine's network without disrupting other work.
2. **Action:** disconnect the network for several seconds, then restore it before all eight reconnect attempts are exhausted.
3. **Expected:** the host remains running, state becomes `Recovering`, bounded reconnect events appear, and state returns to `Listening` after a fresh provider session is configured.
4. **Failure:** host crash, unbounded rapid retries, stale audio replay, duplicate text/audio turn, credential disclosure, or failure to return to `Listening`.
5. **Inspect:** structured events 2101/2102 and sanitized reason codes. Conversational context loss after reconnect is an expected 0.1 limitation.

## 7. Clean shutdown and privacy

1. **Preconditions:** active capture and/or playback.
2. **Action:** enter `/quit`; repeat once using `Ctrl+C`.
3. **Expected:** capture/playback stop, the WebSocket closes, the process exits without an unhandled exception, and audio devices are immediately available to another application.
4. **Failure:** process hangs, audio continues, devices remain locked, or a crash dump/log contains payload data.
5. **Inspect:** final host lifecycle events; search captured logs for known spoken/transcript phrases and API-key fragments. Both searches must return no matches. Remember that interactive transcript lines are console output, so redirected standard output is itself sensitive.
