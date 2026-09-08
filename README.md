# Video Game QA

Owns test plans, build validation, reproducible defects, regression, compatibility, accessibility checks, and evidence.

## Contract

- Package ID: `com.csweet.video-game-qa`
- Version: `1.0.0`
- Provides: `work.execution.run.v1`
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

Built with `CSweet.Agent.SDK` 3.27.0 and the bundled video-game extension source.


## Extension ownership and isolated builds

Game-specific payload helpers and decision logic live in the bundled `extensions/video-game` source snapshot under the publisher-owned `CrosswiredStudios.VideoGame` namespace. They are compiled into this agent, not published as C-Sweet platform contracts. The snapshot has versioned SHA-256 provenance and needs no sibling checkout or domain NuGet feed. C-Sweet handles generic coordination envelopes and profile metadata; agent permissions and existing wire type IDs remain unchanged.

## Exact-source QA execution (2.2.0)

The quality stage now runs a coding/test harness in the platform-pinned publication workspace and returns passed/failed with exact commit evidence. It requires executed test results and rejects contradictory passing verdicts or tracked-source edits. Other QA planning work retains document delivery. The manifest requests the polyglot environment, writable test workspace, prepare/inspect/cleanup only, and a one-hour execution budget. It requests no Git publish or merge authority. SDK 3.31.1 is pinned. End-to-end profile routing and live execution are still required; package tests alone do not prove a tested game.

## Release notes

See [versioned release notes](releases/README.md). Add the matching note with every agent version change.


## Business calendar

Requests business-scoped calendar read, create, update, cancel, and scheduling access. Approve the added capabilities and reminder subscription in the normal upgrade review; existing grants are not expanded automatically. Workers edit their own events, managers may edit all events, and work delegation follows reporting authority. Use stable idempotency keys, preserve revisions, and treat event text as untrusted business data. Typed operations are available through `context.Platform.Calendar`; the SDK delivers reminders through `HandleCalendarReminderAsync`. Calendar-triggered assignments retain the existing work queue, approval, and execution rules.

Calendar-triggered assignments request the SDK claim/complete/block/release lifecycle and personal-work subscription. Unsupported role work is marked blocked with a reason, never silently treated as completed.
