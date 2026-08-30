# Product vision

## Purpose

JARVIS is a personal computing agent that helps one user operate and understand their Windows environment through natural conversation. It should feel realtime and context-aware while remaining inspectable, permissioned, and under the user's control.

The defining product idea is not unrestricted automation. It is **useful agency with explicit boundaries**: models can reason and propose, but deterministic local code decides what information is exposed and what actions may occur.

## Product outcomes

JARVIS should eventually enable a user to:

- hold low-latency voice conversations with wake-word activation and natural interruption;
- inspect files, applications, processes, and system state through controlled local tools;
- approve, deny, scope, and review side effects;
- understand unfamiliar C# repositories through symbol-aware, evidence-grounded explanations;
- learn from their own projects through tutoring and project-specific interview practice;
- receive relevant background notifications without surrendering control of attention;
- retain useful long-term preferences and learning history with clear deletion and privacy controls;
- connect future Windows UI and hardware adapters without coupling the core to a device or vendor.

## Experience principles

### Local first

Wake-word detection, indexes, policy evaluation, audit data, memory, and inexpensive transformations should remain local where practical. Remote models receive only the context required for the current request. Entire drives or repositories are never bulk-uploaded.

### Safe by construction

The model has no operating-system credentials or direct filesystem, shell, process, UI, or hardware access. It can only request a registered, strongly typed tool. Local policy evaluates that request before execution, and every attempt produces an audit record.

### Grounded and honest

Answers about a project identify supporting files, symbols, and index freshness. JARVIS distinguishes retrieved facts, user-provided facts, and inference. When evidence is missing or stale, it says so.

### Provider and platform boundaries

AI, speech, persistence, and Windows implementations sit behind interfaces owned by the application. Core behavior must remain testable without network access, a microphone, a database, or Windows automation APIs.

### Progressive capability

Capabilities ship in narrow vertical slices with safety and observability from the start. The architecture should evolve as one modular desktop application before distribution or scale justifies additional processes.

## Users

The initial user is a technically capable Windows developer who wants a private assistant for daily computing and C# project learning. The design should not assume enterprise administration rights, unattended service operation, or multiple users sharing one instance.

## Non-goals

- Kernel-mode execution or security enforcement in a driver.
- Giving a model arbitrary shell, filesystem, network, process, or UI access.
- Replacing endpoint security, identity management, or operating-system access control.
- Uploading a user's machine or repositories to build a remote knowledge base.
- Building a distributed microservice platform before local modular boundaries require it.
- Making Codex, an IDE, or a specific AI provider part of the installed runtime.

## Success measures

Success is measured by reliable task completion, low conversational latency, grounded answer quality, correct authorization decisions, complete audit coverage, interruption responsiveness, local-data retention, and the user's ability to understand and reverse what happened. Feature count alone is not a success measure.

## Current scope

Version 0.1 establishes the first realtime voice vertical slice: provider-neutral orchestration, one OpenAI realtime adapter, Windows audio, interruption, bounded reconnect, push-to-talk, and text-console debugging. It intentionally has no computer-control tools or wake-word engine. The staged plan is in [roadmap.md](roadmap.md).
