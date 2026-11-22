# Dota 2 and CS2 group stats: implementation plan

Status: Phase 1 released in v0.1.2, Phase 2 (match recaps) in v0.1.3. Phase 3 (weekly digest) and the Game features page implemented on `feature/weekly-digest`. Custom-game ("in-house") features, such as a team
balancer, hero bans and CS2 map votes, are out of scope for now.

## Goals

Groups that play Dota 2 or CS2 get a **Stats** tab with:

1. **Player cards** (Phase 1): rank, recent results, top heroes or maps, and links to each member's
   OpenDota, Dotabuff, FACEIT and Leetify pages.
2. **Match recaps** (Phase 2): when two or more members play the same match, a recap in the tab and,
   if the organizer turns it on, in the group's channel.
3. **Weekly digest** (Phase 3): games played together, win rate and the week's best performer.
4. **Ranks on Live** (Phase 4): the Live tab shows each player's rank next to the game they're playing.

A group counts as a Dota 2 group when Dota 2 (Steam app 570) is one of its games, and as a CS2 group
when Counter-Strike 2 (app 730) is. A group with both games gets both, with a game switch.

## Data sources

| Game | Source | Key | Identity | What it gives |
| --- | --- | --- | --- | --- |
| Dota 2 | OpenDota API (`api.opendota.com/api`) | none needed; optional `OpenDota__ApiKey` raises limits | Steam account → 32-bit account id (SteamID64 − 76561197960265728) | `players/{id}`: rank_tier, leaderboard_rank, persona; `players/{id}/wl?date=30`: wins/losses; `players/{id}/heroes?date=90`: games and wins per hero; `players/{id}/recentMatches`: last 20 matches (Phase 2); `heroes` constants for names and pictures |
| CS2 | FACEIT Data API v4 (`open.faceit.com/data/v4`) | `Faceit__ApiKey` (free server key); CS2 cards are hidden when it is missing | SteamID64 → `players?game=cs2&game_player_id={steamid64}` | skill level, Elo, nickname; `players/{id}/stats/cs2`: matches, win rate, K/D, headshot %, per-map segments; `players/{id}/history`: matches (Phase 2) |
| Links only | Dotabuff, Leetify, Steam | none | account id / SteamID64 | Profile links. Dotabuff has no public API and its pages aren't scraped. Premier rating is not available through an official API. |

Data requirements, shown in the UI when they block stats:

- **Dota 2:** the player's Steam account is linked on My games, and **Expose Public Match Data** is on in
  Dota 2's settings. Otherwise OpenDota has no matches for the account.
- **CS2:** a FACEIT account connected to the same Steam account. Players without FACEIT get a card with
  links only.

Rate limits: OpenDota's free tier (about 60 requests per minute, with a daily cap) is ample. A profile
refresh costs 3 calls, profiles refresh at most every 6 hours, and the refresh job handles at most 20
profiles per run. FACEIT allows far more than this needs.

## Privacy

- **Off by default.** Each person chooses **Show my game stats to my groups** once for their Discord
  account. Until they do, their card shows only their name and a "Not shared" state, and nothing is
  fetched for them.
- Stats are shown only inside groups the person belongs to, to people who can see the group's roster.
- Unsharing removes the cached stats right away.
- Only public data from OpenDota and FACEIT is used.

## Data model

New tables (migration `AddGameStats`):

- `GameStatsSharing (UserId PK, Share INTEGER, ChangedUtc)`: the opt-in.
- `GameStatsCache (UserId, Game TEXT, Payload TEXT, Status TEXT, FetchedUtc, PRIMARY KEY (UserId, Game))`:
  the last fetched profile per game, as JSON. `Status` is `ok`, `private` (no public data), `none` (no
  FACEIT account) or `error`. Error and private results also wait for the next refresh, so failures
  don't hammer the APIs.

Application records:

```text
GameKind: Dota2 | Cs2
DotaProfile(AccountId, Persona, RankTier?, LeaderboardRank?, Wins30, Losses30, TopHeroes[(HeroId, Name, Image, Games, Wins)])
Cs2Profile(FaceitId, Nickname, Level?, Elo?, Matches, WinRate?, KillDeath?, Headshots?, BestMaps[(Map, Matches, WinRate)])
PlayerStatsCard(UserId, Shares, SteamLinked, Status, Dota?, Cs2?, Links)
GroupStats(Game, Cards[], YouShare, CanShare, FaceitConfigured)
```

Requests: `GetGroupStatsQuery(actor, groupId, game)`, `SetStatsSharingCommand(userId, share)`,
`RefreshGameStatsCommand(limit)` (the job), `GetMyStatsQuery(userId)` (for `/stats`).

Infrastructure: `IGameStatsDirectory` with `OpenDotaClient` and `FaceitClient` (named HttpClients,
10-second timeouts, JSON parsing as `internal static` methods so tests can feed real answers), and
`SqliteGameStatsStore`.

Refresh: a card older than 6 hours is queued, and the page shows the cached data with "updated 3 h ago".
The `refresh-game-stats` Quartz job (every 10 minutes) refreshes the oldest stale profiles of sharing
members of Dota 2 and CS2 groups, plus anyone who opened a Stats tab with stale data. Turning sharing on
fetches that person's profile straight away.

## UI and UX

### Group page: Stats tab

The tab appears only when the group plays Dota 2 or CS2. Its icon is a bar chart, and its count badge
shows how many members share stats.

```text
┌ Stats ──────────────────────────────────────────────────────────────────────────┐
│ [ Dota 2 ] [ CS2 ]                       Updated from OpenDota · 5 of 8 share    │
│                                                                                  │
│ ┌ You ──────────────────────────────────────────────────────────────────────┐   │
│ │ ☐ Show my game stats to my groups                          [ Save ]        │   │
│ │   Uses public data from OpenDota and FACEIT for your linked Steam account. │   │
│ │   ⚠ Link Steam on My games first.  /  ⚠ Turn on "Expose Public Match Data" │   │
│ └────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                  │
│ ┌ Kai ─────────────────┐ ┌ Mira ────────────────┐ ┌ Taro ─────────────────┐       │
│ │ [medal] Legend 3     │ │ [medal] Ancient 1    │ │ Not shared            │       │
│ │ 30 days  14W 9L  61% │ │ 30 days  6W 8L  43%  │ │                       │       │
│ │ ▮▮▮▮▮▮▯▯▯ win bar    │ │ ▮▮▮▮▯▯▯▯▯            │ │                       │       │
│ │ [img] Pudge  22 · 59%│ │ [img] Lion  11 · 55% │ │                       │       │
│ │ [img] Lina   15 · 47%│ │ ...                  │ │                       │       │
│ │ OpenDota · Dotabuff  │ │ OpenDota · Dotabuff  │ │                       │       │
│ │ updated 2 h ago      │ │ updated 5 h ago      │ │                       │       │
│ └──────────────────────┘ └──────────────────────┘ └───────────────────────┘       │
└──────────────────────────────────────────────────────────────────────────────────┘
```

- **Cards** are in a responsive grid: three across on desktop, one on phones. Sharing members come first,
  ordered by rank.
- **Dota 2 card:** the medal name and stars (Herald … Immortal, with the leaderboard place for Immortal),
  wins and losses over 30 days with a win-rate bar, and the top 3 heroes by games over 90 days, with hero
  pictures from Steam's CDN, games and win rate.
- **CS2 card:** FACEIT level badge (1–10, coloured like FACEIT's levels) and Elo, lifetime matches, win
  rate, K/D and headshot %, and the best 2 maps by matches. Without FACEIT the card says "No FACEIT
  account" and shows Leetify and Steam links.
- **States:**
  - **Not shared:** muted card with the name only.
  - **Updating:** a spinner line, for cards with no data yet.
  - **No public data:** a hint on how to fix it, shown on your own card only. Other people's cards just
    say "No public data".
  - **Error:** "Couldn't reach OpenDota", with the cached data kept.
- **Your card** is outlined and sits first, with the sharing form directly above the grid.
- **Links** open in a new tab: OpenDota, Dotabuff, FACEIT, Leetify, Steam.
- **Accessibility:** cards are `article` elements, and win-rate bars carry text equivalents. Medal
  colours are never the only signal.

### My games page

A short **Game stats** row under Steam holds the same sharing switch, with links to your OpenDota and
FACEIT pages.

### Discord

- `/games stats [member]`: your own card, or a member's card when they share. Ephemeral text with the
  same fields as the dashboard card. `/games share-stats on|off` changes the sharing choice.
- Phase 2: recaps in the group channel, as an embed with the result, each member's hero/KDA or K/D, the
  duration and a match link.

### Phase 2: match recaps (implemented)

As built:

- **Reading history:** `refresh-matches` runs every 10 minutes. It reads each sharing person's history at
  most every 30 minutes, at most 20 histories per run, oldest first:
  - Dota 2: OpenDota `recentMatches`, one call that already includes the hero, K/D/A and the result.
  - CS2: FACEIT `history` (last 10 matches) plus one `matches/{id}/stats` call per new match.
  - Matches are kept 30 days in `PlayerMatches`.
- **Finding recaps:** a match id saved for two or more sharing members of a group becomes a recap. Members on
  opposite sides show as "Opposite sides", with each member's own result.
- **Posting:** a recap is posted at most once (`GroupRecaps`), only if the match started in the last day, and
  only when the organizer turned the channel on (`RecapChannels`). Recaps are recorded even while posting is
  off, so turning it on later doesn't flood the channel with old games.
- **Privacy:** turning sharing off deletes that person's saved matches too.

The original design:

- **Detection:** the job reads each sharing member's recent matches (OpenDota `recentMatches`, FACEIT
  `history`). A match id shared by two or more members of the same group becomes a recap. Recaps are
  stored per group and match, so each is posted once.
- **Stats tab:** a **Recent games together** list, with result, date, heroes or maps, and per-member
  lines.
- **Channel posting** uses the group's notification route, with an organizer switch like Live's. It is
  off by default.

### Phase 3: weekly digest (implemented)

As built:

- **Stats tab:** a **Last 7 days** box per game, rolling: games together, the group's record (games on opposite
  sides count as neither a win nor a loss), the most played hero (Dota 2, across members' picks) or map (CS2,
  per match), the best performer (highest win rate with at least 3 games), and each member's games, wins and
  K/D/A.
- **Monday post:** a second organizer switch, off by default. The hourly `post-weekly-digests` job posts last
  week (Monday to Sunday in the server's zone) on Monday from 10:00 local time, once per group and week
  (`GroupDigests`, claimed before posting). It skips weeks without games together and posts only on Mondays, so
  turning it on midweek doesn't post an old week.

The original design:

On Monday morning in the server's time zone, groups with channel posting on get: games together, wins
and losses, the most played hero or map, and the best performer (highest win rate with at least 3
games). The Stats tab shows the same summary for the last 7 days.

### Game features page (implemented with Phase 3)

- **Page:** `/game-features/{guild}` in the side panel, with:
  - **Your game stats:** the same cards and sharing switch as group Stats tabs (`GetMyStatsQuery`,
    `SetStatsSharingCommand`), so both places stay in sync. A **Refresh my stats** button re-reads cards and
    matches.
  - **Dota 2 · Meta heroes by lane:** OpenDota `scenarios/laneRoles` (lanes 1–3), summed over game-length
    buckets. Heroes with at least 0.5% of a lane's games (minimum 100) are ranked by win rate, top 5 per lane.
    The data is cached for 6 hours for the page and 1 hour for posts.
  - **Posting (server admins):** choose a channel or create `#dota-meta`, **Post now**, and a schedule (off,
    every day, every two days, or weekly on a chosen day) from a chosen hour in the server's zone. The
    `post-dota-meta` job runs hourly; each due day is marked before posting, so a broken channel isn't retried
    every hour.
  - **Meta search (STRATZ, needs `Stratz__ApiKey`):** filters for patch (current or last 7 days), role
    (any or position 1–5), rank group (all, Herald–Guardian, Crusader–Archon, Legend–Ancient,
    Divine–Immortal) and order (meta score, win rate, pick rate). It uses `heroStats.winDay` (or `winWeek` once the
    patch is more than a week old) with `gameModeIds: [ALL_PICK_RANKED]`, `groupBy: HERO_ID`, `positionIds` and
    `bracketIds`.
    - STRATZ's own patch list lags (7.41f while OpenDota already lists 7.42), so the current patch's start date
      comes from OpenDota's `constants/patch`, and the daily rows are summed from the first full day after the
      release.
    - Meta score is the lower bound of the 95% Wilson interval of the win rate. Heroes with under 0.25% of the
      role's games are left out.
    - Each filter is one request, cached for an hour; every order shares the same cache entry.
    - Scheduled posts use the admin's saved patch, rank and order, with one column per position. If STRATZ fails,
      posts fall back to the OpenDota lane meta.
  - **CS2:** account stats now; more features later.
- **Group Stats tab:** a **Refresh** button re-reads the group's sharing members, the viewer first, at most 8
  people per click and skipping anything fetched in the last 5 minutes.
- **Failure reasons:**
  - Every OpenDota or FACEIT failure keeps a one-line reason: the service, the endpoint (never the key), the
    HTTP status and the start of the answer, plus a hint for 401/403/429.
  - The reason is stored with the card (`GameStatsCache.Reason`), shown under **Why?** on your own card, and
    logged with `UserId`.
  - Refreshes started from a server page also carry `GuildId`, so they appear in that server's Logs. The
    background jobs' entries are bot-wide and visible to `Logs__OwnerIds`.

### Phase 4: ranks on Live

Live rows show the rank badge of sharing players, from the cached cards, so no extra calls are made.

## Phase 1 scope (this branch)

1. Migration, store and records; OpenDota and FACEIT clients with parsing tests from real answers.
2. Sharing opt-in on the group Stats tab and on My games; the cache, on-demand refresh and the refresh
   job.
3. Stats tab with Dota 2 and CS2 cards, every card state, and the game switch.
4. en/uk/ru texts, the guide section, `bot.env.example` (`Faceit__ApiKey`, optional `OpenDota__ApiKey`),
   and tests for sharing, privacy (no data without opt-in), parsing, rank names and refresh choice.
5. `/games stats` and `/games share-stats` commands.

Phases 2–4 follow in later releases, each with its own release notes.
