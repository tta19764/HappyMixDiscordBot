# Main-team esports development plan

Goal: extend the current schedule/reminder/result pipeline to NAVI, FUT and Monte main rosters in CS2 and Dota 2, excluding academy/junior identities.

## Verified provider identities — 2026-11-09

Queried the authenticated PandaScore `/csgo/teams` and `/dota2/teams` endpoints by organization name. Token values are never recorded here.

| Game | Organization | Provider ID | Exact slug | Coverage |
| --- | --- | --- | --- | --- |
| CS2 | NAVI | 3216 | natus-vincere-cs-go | Main team found |
| CS2 | FUT | 136955 | fut-esports-cs-go | Main team found |
| CS2 | Monte | 131441 | monte | Main team found |
| Dota 2 | NAVI | 1699 | natus-vincere | Main team found |
| Dota 2 | Monte | 132557 | monte-dota-2 | Provider identity found; not proof of active roster or upcoming fixtures |
| Dota 2 | FUT | — | — | No matching main roster returned; do not substitute Future Gaming or Future.club |

Excluded examples: FUT Academy (137462), ex-FUT Academy (138023), FUT Turkuaz (137202), Monte Gen (133943), MonteCarlo and Montenegro. Match inclusion requires an opponent with a configured exact ID; juniors can appear as the *opponent* of a followed main team, but are never independently followed.

## Implementation sequence

1. Introduce a game-neutral match model and game-qualified event identities.
2. Configure verified team IDs separately per discipline; fetch both APIs with pagination and strict team filtering. All tiers are followed by default; retain an optional tier restriction.
3. Reuse the existing synchronization pipeline for schedules, starting-soon alerts, completed maps/games and series results. Distinguish disciplines in Discord embeds and deduplication.
4. Add XML documentation for new/changed models, contracts, options and adapter behavior.
5. Add fixture-based provider, scheduler and formatting tests for main teams, junior exclusion, mixed games, pagination, boundaries and duplicate events.
6. Run the build/tests, update README and the roadmap, regenerate the dashboard. Perform read-only live API validation if permitted; do not send test messages to the real Discord channel.

## Acceptance and limits

- CS2 title filter stays CS2-specific; Dota requests never receive that filter.
- A match between two followed teams appears once. Identical numeric IDs in different games cannot suppress each other's events or merge tournament pages.
- Upcoming and past requests use Kyiv local-day bounds with an exclusive upper bound; running matches from the previous day can still finish after midnight.
- Pending/cancelled matches do not generate starting-soon messages. Completed games discovered at series completion are included.
- Missing/failed provider responses do not silently count as an empty successful feed. Pagination has a safety ceiling and fails rather than truncates silently.
- Persistent deduplication/SQLite, guild subscriptions, reschedule edits, spoiler preferences and watch parties remain separate roadmap work. Delivery state is still process-local for this increment.
- Provider team lookup is not a claim that all matches or game-level results are available under every API subscription.

## Implementation result

- Steps 1-6 completed for the source change. Exact team ID defaults are explicit in appsettings.json and PandaScoreOptions; no fuzzy name matching is used.
- All 57 automated tests passed under installed SDK 10.0.402. The repository pin remains 10.0.102; an ignored work/sdk-validation directory selected the installed SDK for validation. A structured logging argument was adjusted for the installed SDK's CA1873 analyzer.
- Read-only live checks of upcoming/running/past for both disciplines succeeded, including CS2 title filtering and past end_at ranges. These checks used the configured token without logging it.
- No Discord messages were sent during validation and no production deployment is claimed.
- Regression coverage includes exact-ID exclusion of academies and lookalikes, optional tier filtering, JSON configuration replacement/disable semantics, pagination and its safety limit, malformed/null responses, cancellation, Kyiv DST days and exclusive boundaries, cross-midnight matches, duplicate events across disciplines, reminder lifecycle/window checks, delivery retries and discipline-specific message formatting.
- Follow-up: Day 1 SQLite persistence is now implemented; see SQLITE_DAY1.md. The earlier esports-only increment used process-local history. Per-guild subscriptions remain partial in the broader roadmap.
