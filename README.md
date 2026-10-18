# Video Game QA

Owns test plans, build validation, reproducible defects, regression, compatibility, accessibility checks, and evidence.

Quality stages accept the Producer's canonical workflow assignment even when the
original approved plan delegates only implementation. Standalone specialist QA
still requires its planning delegation. Exact-source, eligibility and executed-test
evidence remain mandatory; assignment recovery does not imply a passing verdict.

## Contract

- Package ID: `com.csweet.video-game-qa`
- Version: `4.0.6`
- Provides: `work.execution.run`
- Activation: manual
- Requested platform/provider capabilities: none
- Event subscriptions: none
- Network access: none

## Develop

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.QA.VideoGame -- --self-test
```

The tests run entirely in memory and require no C-Sweet instance or credentials.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the exact manifest, grants, activation mode, and source before approving installation.

Built with `CSweet.Agent.SDK` 3.65.0 and the bundled video-game extension source.


## Extension ownership and isolated builds

Game-specific payload helpers and decision logic live in the bundled `extensions/video-game` source snapshot under the publisher-owned `CrosswiredStudios.VideoGame` namespace. They are compiled into this agent, not published as C-Sweet platform contracts. The snapshot has versioned SHA-256 provenance and needs no sibling checkout or domain NuGet feed. C-Sweet handles generic coordination envelopes and profile metadata; agent permissions and existing wire type IDs remain unchanged.

## Exact-source QA execution (2.2.0)

The quality stage now runs a coding/test harness in the platform-pinned publication workspace and returns passed/failed with exact commit evidence. It requires executed test results and rejects contradictory passing verdicts or tracked-source edits. Other QA planning work retains document delivery. The manifest requests the polyglot environment, writable test workspace, prepare/inspect/cleanup only, and a one-hour execution budget. It requests no Git publish or merge authority. SDK 3.65.0 is pinned. End-to-end profile routing and live execution are still required; package tests alone do not prove a tested game.

## Release notes

See [versioned release notes](releases/README.md). Add the matching note with every agent version change.


## Business calendar

Requests business-scoped calendar read, create, update, cancel, and scheduling access. Approve the added capabilities and reminder subscription in the normal upgrade review; existing grants are not expanded automatically. Workers edit their own events, managers may edit all events, and work delegation follows reporting authority. Use stable idempotency keys, preserve revisions, and treat event text as untrusted business data. Typed operations are available through `context.Platform.Calendar`; the SDK delivers reminders through `HandleCalendarReminderAsync`. Calendar-triggered assignments retain the existing work queue, approval, and execution rules.

Calendar-triggered assignments request the SDK claim/complete/block/release lifecycle and personal-work subscription. Unsupported role work is marked blocked with a reason, never silently treated as completed.

Producer-owned ticket estimates use a board conversation with QA. QA invites the Producer to author estimates for the exact requested scope, checks the returned coverage, and records its review. Estimate provenance identifies the Producer's original artifact; this review does not replace the separate QA sprint-readiness assessment.


Version 2.4.6 runs repository-finalized standalone QA tickets through the existing brokered workspace and test harness. It validates the exact prepared revision, rejects tracked source changes, requires observed evidence for each assigned acceptance criterion, and submits a durable report revision for Producer review. Failed checks block the ticket. Tickets without repository delivery remain planning documents. The existing prepare/inspect/cleanup and artifact capabilities suffice; no new grants are requested.

## Isolated QA workspace (2.4.6)

SDK 3.65.0 downloads the broker-authorized exact revision into the isolated runtime. The workspace-sync declaration is used only for downloads; the broker continues to deny QA source uploads and publication. Review that additional declaration through the normal update flow. QA compares original file hashes against the downloaded snapshot before and after executing tests, rejects modified/missing source and symbolic links, and keeps generated reports local. Remote workspace inspection remains an additional check.

## Reliable QA report submission (2.4.7)

The local `submit_qa_report` tool records evidence at the fixed `.csweet/qa-outcome.json` path after checking the assigned commit, actual validation commands, verdict consistency, and complete criterion coverage. It treats report strings as data rather than shell commands. Confined reads do not request unattended approval; generic file writes are still not automatically approved. Source integrity is independently checked after validation. Missing reports and pending tool approvals produce specific blockers. No new platform grants are requested, and absent measurements are never treated as passing evidence.

## Report argument compatibility (2.4.8)

The report tool now exposes flat named arguments and accepts a single JSON encoding layer for booleans and arrays from model tool parsers. The previous nested object remains compatible. Invalid arguments or evidence return specific feedback with the assigned criteria; corrections must preserve actual results. Regression coverage exercises the live payload format through the complete harness on Windows and Linux. Exact commit, criterion coverage, verdict consistency and source-integrity checks still gate acceptance.

## Snapshot provenance context (2.4.9)

QA receives the broker-authorized workspace and commit identity plus the number of original files verified before its run. Downloaded snapshots intentionally omit `.git`; independent source-hash and broker checks establish source integrity. The model must still execute relevant tests and assess every criterion. Snapshot identity does not establish branch membership, merge completion or passing measurements.

## Blocked for a decision, not failed back to engineering (2.5.0)

A human QA lead does not send a build back to the developer because the test lab lacks a device. Each criterion in the QA report may now set `unverifiable=true` (always with `satisfied=false`). This means the evidence needs a device, browser, network, registry, service or tool that is unavailable here, as opposed to a defect or evidence the engineer could have produced.

- Any failing command, ordinary unmet criterion, or finding still returns `failed`, which routes the candidate back to engineering. Findings are reserved for defects; a check that can't be performed here is recorded only as an unverifiable criterion.
- If every command passes and the only gaps are unverifiable criteria, QA returns **Blocked** with diagnostic `decision-required:v1`. The summary names each unverifiable criterion, what is missing, and the options: defer or amend the criterion, or provide the environment. The platform and Producer route this to a decision maker, and QA is retried on the same candidate once it is resolved.
- The report renders these checks as "not verifiable in this environment", never as passed.

This release includes the unreleased 2.4.9 snapshot-provenance changes. There are no new grants or network access.

## Ticket discussion (2.7.0)

Answers directed ticket questions using current ticket context and recorded review evidence. Responses clarify findings without changing review decisions, acceptance criteria or validation requirements.

The agent subscribes to `com.csweet.work.item.discussion.changed.v1` and uses grant-governed item,
comment, board and orchestration reads. Ask with `@Full Name: your question` on a team ticket.
Replies use `discussion.reply` and an exact comment/revision correlation; duplicates and stale
requests are ignored. Informational notifications do not trigger automatic conversation loops.
Bounded attention recovery scans the current assigned project for missed requests. The host retains
undelivered notifications while offline; install the matching C-Sweet host update and approve new
manifest permissions. Comments provide context, never execution authority or review approval.

## Hierarchical delivery

The V2 assignment identifies Task, Story, Epic or Release scope. Tasks require an authorized sprint; aggregate reviews require an active delivery plan and do not invent sprint identities. Use the exact assigned branch, commit or artifact revision. Every task requires independent QA, including documents. Managers accept aggregate candidates using current evidence; task completion does not promote to main or authorize deployment.

Model token settings default to 256,000 context-window tokens (maxContextWindowTokens) and 128,000 maximum output tokens (maxOutputTokens). Explicit installation settings override these defaults.

## Delegation and tracking

SDK 3.65.0 dispatches authenticated delegation before specialist conversation fallback. The manifest requests delegation, work tracking, directory and conversation reads, personal commitments and model interpretation. These declarations require reviewed grants; existing resource permissions and approval rules still apply. The shared SDK owns coordinator reporting and wake recovery.

## Independent task QA (4.0.0)

Task QA reads authorized current planning and delivery before tests and again before accepting a result. It emits one canonical `WorkDeliveryReviewResult` bound to the exact integrated story commit, with criterion and repository-bound command evidence. The report tool accepts flat named arguments with JSON booleans and arrays; nested or encoded legacy report shapes are rejected with feedback.

The manifest requests `runtime.internet.public.v1` and HTTPS GET access to `registry.npmjs.org` for fresh npm restores. Approve this request through normal installation/grant review before retrying independent QA. It adds no credentials, host/private access, merge or deployment authority.

## Execution contract

Execution uses one `work.execution.run` capability and `WorkExecutionAssignment` from SDK/WorkManagement.Contracts 4.0.0. Task and aggregate scopes retain their distinct authority checks within that envelope. Upgrade the platform and executing agents together; no legacy execution adapter remains.

## Artifact review recovery (4.0.5)

Consumes SDK 4.6.4. Deploy the matching platform dispatch/grant fix before retrying artifact QA. The reviewer receives the current author result and authorized retry evidence, rejects ambiguous history, and requires independent version-specific evidence for API and feasibility claims. Existing capability and network requests are unchanged.

## Bounded report correction (4.0.5)

A passing report requires `findings=[]`; findings are unresolved blockers only. Keep nonblocking observations and optional verification notes in the summary, and cover only the exact assigned acceptance criteria. The tool returns combined report-consistency and criterion-coverage feedback. A linked cancellation budget stops the actual harness after three failed corrections; exhaustion becomes a C-Sweet report-processing issue for management instead of an indefinite report loop. Real defects and failed commands still prevent approval, and every retry produces fresh exact-source evidence.
Artifact report tasks may receive a pinned read-only repository input through delivery evidence. They retain Artifact delivery and independent acceptance; they do not prepare or publish a Git workspace. Missing browser or device evidence remains explicitly unverifiable.

Unattended QA disables generic file-write/edit/delete tools. `save_qa_test_plan` writes only `.csweet/qa-test-plan.md`; `submit_qa_report` writes only the assigned structured report. Original source remains hash-checked. Each observed test-command result is checkpointed before the tool returns, and validated reports survive lost replies. Recovery journals are bound to source files, planning, scope and assignment revision; owner-retained audit observations are labeled historical data and cannot supply missing browser/device evidence or grant a pass.

## Artifact criterion correction (4.0.6)

SDK 4.6.4 applies bounded JSON correction to the separate artifact-review path. The result must quote each exact acceptance criterion once; requirements and review checklists are evaluated in evidence, summary and findings. Identity, criterion coverage and verdict consistency receive specific correction feedback. Up to three corrections preserve actual defects and use the same delivered revision; current planning and document authorization are checked again before a verdict is returned. Exhaustion is reported to management as a C-Sweet artifact-QA processing issue.
