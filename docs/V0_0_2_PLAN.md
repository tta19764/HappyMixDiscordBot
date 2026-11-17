# v0.0.2 implementation plan

Work branch: `feature/v0.0.2-notifications`; submit a PR to main before tagging.

1. Centralize channel creation under a reused or newly created `Bot Notifications` category. Apply it to news, releases, esports, group, and event channels. Add create-channel controls alongside existing-channel selectors, validating permissions.
2. Persist recurring-series participation separately from occurrence overrides. One acceptance covers future occurrences; withdrawing one date leaves the series intact. Update invitations, dashboard, commands, availability projections, and migration tests.
3. Add per-game notification language and optional Twitch discovery settings. Allow selection and ordering of supported criteria (language, broadcaster tags including self-declared country, audience size). Require live status and event relevance. Display discovered broadcasts separately from selected streamers within reminders, match starts, and map results. Country tags are not verified geography.
4. Add independent group and event notification settings with explicit inheritance. Support existing/new channels and an enable switch. Send deduplicated event reminders; mention participating members only when they are absent from every server voice channel. Enable voice-state access and handle unavailable membership conservatively.
5. Publish all Steam news feeds, preserving delivery deduplication and subscription creation cutoffs. Check pagination so a busy feed cannot silently lose announcements.
6. Keep dashboard and command workflows aligned. Update English, Ukrainian, and Russian resources, feature guides, /help, documentation, and progress tracking.
7. Test persistence upgrades, recurring RSVP overrides, channel creation/reuse, stream ranking, localization, news delivery, and notification deduplication. Run the solution checks and release manifest validation.
8. Prepare accurate en/uk/ru `v0.0.2` release notes, push the feature branch, and open a PR. After validated merge, create the new immutable tag and verify deployment and Ukrainian release delivery to server 887398817404694548.

## Acceptance details

- Existing channels are not moved automatically; all newly created channels use the category.
- Recurrence defaults and occurrence withdrawals survive restarts and invite retries.
- Disabled discovery makes no search requests. Selected streamers continue to appear independently of discovery ranking.
- Existing notification settings retain their current behavior until explicitly configured.
- Steam news includes general announcements, not just updates/releases/beta notices.

## Implementation status

Implemented: shared category-aware creation, per-game channels/language/discovery, recurring-series acceptance with per-date withdrawals, group/event notification routes and voice-aware reminders, all-feed Steam pagination, dashboard and command controls, localized guides and regression coverage.

Validation: 210 tests pass in Release configuration, including migration upgrade/restart coverage, route isolation, stream ranking, and localization. Release manifests and generated progress checks pass. Pull-request CI and deployment verification remain. The v0.0.2 tag must point to the PR merge commit on main, never the feature-branch head. No existing version tag will be moved.
