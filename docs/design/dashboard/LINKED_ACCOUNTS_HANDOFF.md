# Day 7: linked alternate accounts (twinks), handoff

Status: implemented; 142 tests pass (14 new in `AccountLinkTests`), including the architecture tests. Pages
checked with mock data at desktop and phone widths in English and Ukrainian. **Live Discord verification is Day 8.**

## Linking flow and ownership

1. The primary account opens **Linked accounts** and creates a link. HappyMix stores a request with a
   SHA-256 hash of a 32-byte random secret; the link (`/links/{guild}/approve/{id}?t=…`) is shown once.
2. Someone opens the link **signed in to Discord as the alternate account** (Discord OAuth). The page shows a preview.
3. The alternate resolves any conflicts and approves.

Rules (Application, `AccountLinkHandlers`):

- Requests expire after **15 minutes**, are **single use** (an atomic `ConsumedUtc IS NULL` update), and can be revoked.
- The acting account always comes from the session. IDs, usernames or the link alone never link anything.
- Both accounts must be human members of the server (checked through Discord at approval).
- Links are per server. No self-links, chains or cycles (an alternate can't have alternates or be linked twice);
  up to 5 alternates and 3 open requests per primary.
- Unknown IDs, wrong secrets, other servers and expired or used requests all get the same answer.
- A retry by the same alternate after success returns "already linked". Concurrent approvals link once (tested).

## One member per family

Every group and availability handler resolves the requesting account to the family's primary account
(`MemberLinks`) before applying rules, so the dashboard and slash commands behave the same.

- **Groups.** Membership and organizer authority are shared. Invitations are addressed to one Discord account and
  only that account can accept or decline. The accepted account is stored as the group representative.
- **Availability.** One schedule, stored on the primary account; reads and writes from any linked account use it. The group
  board shows one row per member and counts each member once per hour.
- **Permissions.** Discord admin and moderator rights come only from the account making the request
  (`GroupActor.CanModerate` is never inherited). Settings and role repair keep their existing checks.
- **Discord roles.** Only the recorded group representative is eligible for the role. Link and unlink reconcile
  both accounts. Manual role changes never touch membership; role repair restores roles from saved data. If
  saving succeeds but Discord fails, the page says so and points to role repair.

## Existing data: what's shared and what the alternate keeps

| Record | On link | On unlink |
| --- | --- | --- |
| Group memberships held by either account | Stay saved under their own account; counted once | Each account keeps the memberships saved under its own ID. The alternate loses access it had only through the family. |
| Memberships and groups created while linked | Saved under the primary | Stay with the primary |
| Organizer of groups the alternate created before linking | Shared after explicit acknowledgment | The alternate still organizes them |
| Invitations | Duplicates for the same member are merged (listed in the preview) | Pending invitations stay with whichever account holds them |
| Weekly hours and one-off windows | Moved to the primary. Overlaps need an explicit choice per entry; identical entries merge | The primary keeps the shared schedule. The alternate gets back its saved pre-link schedule (weekly hours and windows that haven't ended). |

The alternate's full pre-link schedule is stored as a snapshot in the link record (`AccountLinks.Snapshot`), so nothing
is discarded silently. Linking runs in one SQLite transaction: consume the request, insert the link, move availability,
merge invitations.

## Routes

`GET /links/{guild}` · `POST /links/{guild}/requests` · `POST /links/{guild}/requests/{id}/revoke` ·
`GET|POST /links/{guild}/approve/{id}` · `GET|POST /links/{guild}/unlink/{alternateId}`

## Storage

Additive migration `20261116000100_AddAccountLinks`: `AccountLinks` (key `GuildId, AlternateId`) and
`AccountLinkRequests` (token hash only). No existing tables change.

## Presentation

Rosters, invitations, the directory's organizer and the availability grid show the recorded group account first with up to two small linked
avatars and `+N`. The linked group is focusable, with a tooltip and an accessible label listing every linked name. Linked
names are only returned to viewers who can see that roster.

## Pending live verification (Day 8)

- Two real Discord accounts linking through two sign-ins (private window), including the "switch account" page.
- Single-recipient invites and account-specific role repair on a real server; partial-failure banner.
- Slash commands from an alternate (`/group list`, `/availability set today`) acting on the shared member.
- Server Members Intent for names and avatars.
- The token sits in the approval URL's query string; check that proxy logs (Caddy) don't keep it longer than its 15 minutes of use.
