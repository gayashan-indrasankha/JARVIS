# Manual Project Tutor and Mock Interview smoke test

This test is required because the default automated suite uses fake models and no physical audio/GPU. Record actual results; do not report success from code inspection alone.

## Preconditions

1. Use Windows x64 with .NET 10 and a disposable C# Git repository containing no production secrets.
2. Configure its absolute path as `JARVIS_Tools__AllowedRoots__0`.
3. Install/diagnose the required FAST runtime/models with `./scripts/setup-local-ai.ps1 -DownloadRuntime -DownloadModels` and `./scripts/diagnose-local-ai.ps1`.
4. For the optional profile test only, run `./scripts/setup-local-ai.ps1 -DownloadDeepModel`, set `JARVIS_LocalAi__Deep__Enabled=true`, and rerun diagnostics. DEEP is not required for acceptance.
5. For voice, use a microphone and headphones, enable `JARVIS_Voice__Enabled`, `JARVIS_Voice__SpeechOutputEnabled`, and optionally wake listening.
6. Start with no private console redirection. Note the configured `JARVIS_HOME`; logs and databases are under it.

## FAST text acceptance flow

1. Start `dotnet run --project src/Jarvis.Host/Jarvis.Host.csproj`.
2. Enter: `Analyze this repository.` Expected: authorized index success with bounded counts. Failure: repository rejected, build/script execution, or invented success. Inspect console and content-free tool audit logs.
3. Enter: `Teach me its architecture.` Expected: a tutor session ID, `PROJECT FACT` statements with real relative file/line evidence, and progressive explanation. Failure: generic-only lecture, fabricated path/line, entire source dump, or no evidence.
4. Enter: `Don't tell me the answer. Ask me questions.` Answer once, then enter `Go deeper`, `Show me the actual code that proves that`, and `Recap my weak areas`. Expected: same session ID, Socratic turn before answer, deeper level, exact current evidence, tracked gaps/recap. Failure: lost state, invented evidence, or continuous lecture.
5. Enter: `Now interview me about it for a .NET internship. Ask five questions.` Answer question one superficially and one question incorrectly; answer later questions with equivalent concepts in different wording. Expected: project-specific questions, a targeted follow-up before correction, evidence-grounded eventual correction, no wording penalty, ten 0–4 rubric dimensions, and readiness after five completed answers. Failure: immediate answer reveal, generic questions despite evidence, arbitrary/out-of-range score, or sixth required answer.
6. Enter: `End the interview and show my weaknesses.` Expected: the ten documented report categories, strengths, weaknesses, poorly answered questions, revision topics, and suggested next difficulty. Failure: missing category or completion claim before tool success.
7. Enter: `Teach me everything I got wrong.` Expected: a new ask-before-tell tutor session using a recorded weakness and current repository evidence. Failure: no prior-report linkage or unrelated generic lesson.
8. Enter `/quit`. Expected: clean host and managed llama-server shutdown.

## Voice and offline flow

1. Complete setup while connected, exit JARVIS, disable Wi-Fi/Ethernet, and verify an Internet URL fails.
2. Start JARVIS offline. Say “Jarvis”, request repository analysis, start the same five-question interview, answer and interrupt at least one spoken evaluation, end/report, and request revision.
3. Expected: wake → local ASR → local tool/Project Intelligence/model → local TTS for every turn; barge-in stops stale audio; no cloud/API-key/network dependency; all evidence remains local.
4. Failure: external connection attempt, missing API key, stale audio resumes, lost session, raw tool JSON/reasoning spoken, or ungrounded project correction.
5. Inspect local logs for lifecycle/timing and tool IDs only. They must not contain transcript, answer, prompt, source excerpt/path, audio, hidden reasoning, or model-process credential.

## DEEP routing and fallback matrix

Run each as a separate session; watch process/memory locally without recording machine identity.

| Case | Action | Expected | Failure indicator |
| --- | --- | --- | --- |
| Disabled | Request DEEP with default `Enabled=false` | FAST session, `deep_disabled` fallback | crash or unusable session |
| Missing | Enable DEEP without its GGUF | FAST, `deep_not_installed` | runtime download or crash |
| Low memory | Set threshold above currently available RAM | FAST, `deep_memory_insufficient` | DEEP start attempt |
| Installed | Install valid 8B and request DEEP | current server stops, one DEEP server health-checks, same process remains for all questions | two LLM servers or per-question restart |
| Load failure | Temporarily use insufficient offload/context while preserving valid file | bounded attempt then FAST | restart loop, orphan, host crash |
| Session end | End a successful DEEP session | DEEP stops and FAST becomes active/on-demand | DEEP remains alongside FAST |

Do not modify the manifest or substitute an unverified model to force these tests.

## Persistence, privacy, and cleanup

1. Complete an interview, exit, restart, and request revision for the same repository. Expected: the latest completed report loads from `Data\ProjectLearning\project-learning.db`.
2. Set `JARVIS_ProjectLearning__PersistSessions=false`, run a short session, restart, and request revision. Expected: no persisted recovery.
3. Stop JARVIS, copy any desired report, delete only `JARVIS_HOME\Data\ProjectLearning\project-learning.db`, restart, and confirm old sessions are unavailable. Failure: DB lock after shutdown or files created inside the repository.

## Measurements to record

Record profile, context, GPU layers, available RAM before load, model cold-load/health time, warm first-token, question-generation latency, answer-evaluation latency, TTS first audio, end-to-end answer-to-speech, and barge-in stop time. Also record peak RAM/VRAM and whether any process was orphaned. Use `NOT PHYSICALLY MEASURED` for anything not measured; never invent values.

Status until completed on real hardware: **USER MANUAL VERIFICATION REQUIRED**.
