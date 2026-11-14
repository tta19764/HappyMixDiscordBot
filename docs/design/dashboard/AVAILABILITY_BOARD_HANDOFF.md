# Group availability board and easier availability entry: handoff

Status: implemented, builds, **127 tests pass** (8 new; the 3 architecture tests pass). Checked in a local preview
with mock members at desktop and phone widths in English and Ukrainian. **No storage migration.** Not yet tried
against a live Discord server.

## What changed

### Group availability board (group page → Members)

- An **Availability** table now sits at the top of the Members section, above the **Roster** (which keeps every
  membership action). A "Manage members ↓" link jumps to the roster.
- Seven day tabs, today first, each labelled with weekday and date in the viewer's language. The chosen date is in
  the URL (`/groups/{guildId}/{groupId}?day=2026-11-16#availability`); unknown or out-of-range dates fall back to
  today. Tabs are links with `aria-current="page"` on the selected one, arrow/Home/End keys move between them, and
  on phones the strip scrolls horizontally with the selected tab kept in view.
- One row per saved group member, every day: avatar, display name, all ranges for the date. Members with
  availability come first; within each part the order is organizer, then name, then user ID, so it is stable.
- No availability → the row stays, with a muted background and **No data**. The app has no explicit
  "unavailable" status, so No data is the only empty state.
- Each range is labelled **One-off** (dated, saved for that day) or **Weekly** (dashed outline: "usual hours, not
  confirmed for this date"). Full days show **All day**. Ranges cut at midnight show ↤ (from the previous day) or
  ↦ (continues the next day), with screen-reader text.
- The board's time zone is shown above the table ("Times in Europe/Kyiv"): the server time zone from Settings,
  falling back to the deployment default.

**Override rules:** the app has no rule where a one-off window replaces weekly hours (they are stored and shown
independently everywhere). The board therefore shows both, labelled, and applies no override.

### Easier availability entry (Today and the weekly day editor)

- Times are now hour and minute selects. An empty editor starts at **18:00**; an empty end has no hour and **00**
  minutes. Editing a saved weekly day keeps its times, and validation errors keep what was submitted.
- **Free all day** checkbox next to the time controls in both editors. It disables the time selects, previews
  "Today 00:00 → Tomorrow 00:00 · All day · 24h" and is saved with the normal Save button. Works without
  JavaScript too.
- One-off full day = local midnight on the selected date to local midnight on the next date in the chosen time
  zone (23 or 25 hours on daylight-saving days). Weekly full day = 00:00–00:00 in its zone, resolved per date, so
  it stays anchored to local midnight. Ordinary ranges like 18:00–00:30 keep their meaning.
- Full days show as **All day** on the personal Today list, the weekly timetable (full bar), the server picker
  status line, `/availability timetable` in Discord, and the group board.

## Authorization and data access

- `GetGroupAvailabilityBoardQuery` (Application) re-applies the roster rule: group members, the organizer and
  server moderators only. Invitees, outsiders, other servers and unknown IDs get `Group.NotFound`, whether the
  request comes from the page or a crafted URL. The controller also only asks for the board when the detail
  query says the roster is visible.
- Reads are batched: `IAvailabilityStore.ListForMembersAsync` runs two queries (one-off windows overlapping the
  selected day, and weekly rows) for the group's saved members only. Nobody else's availability is read.
- Each weekly occurrence is resolved in the member's own zone on its own date, then converted to the board
  zone before choosing the day. That covers zones that cross midnight, overnight carryover, several ranges and
  DST gaps and repeats (`LocalTimes.ToInstant`: gap times move forward, repeated times use the earlier instant).

## Storage

No migration. A weekly full day is stored as `StartTicks = EndTicks = 0`; equal start and end was previously
rejected, so no existing row has that meaning. A one-off full day is an ordinary UTC window.

## Tests added (`GroupAvailabilityBoardTests`)

- Board access for member, organizer and moderator; denied for invitee, outsider, another server and unknown ID.
- Every member gets a row; members without data come last; other people's availability never appears.
- Seven-day window and fallback for out-of-range dates.
- A New York Sunday evening appears on Monday in Kyiv (across a DST change).
- Overnight weekly range split across midnight, plus multiple ranges and a one-off on the same day.
- Weekly full day on the 25-hour Kyiv fall-back day stays exactly one board day and doesn't spill into Monday.
- Saving Free all day adds one window without changing weekly hours or other windows; 18:00–00:30 still means
  6.5 hours; weekly full day replaces only that day; equal start and end is still rejected for ordinary ranges.

## Localization

21 new strings in `DashboardResources.uk.resx` and `.ru.resx`. Weekday and date labels on the tabs use the
request culture's own formats.

## Needs live verification

1. Board names and avatars come from the same member list as the roster, so they need the Server Members Intent
   (otherwise "Member · …1234").
2. Saving Free all day from the dashboard and confirming it on the personal page, the group board and in
   `/availability timetable`.
3. A real server time zone other than the deployment default, and a member who saves weekly hours in a different
   zone.
4. The hour/minute selects on iOS and Android pickers.

## Update: hour grid (replaces the list-style board)

Design: canvas row 6, "Group availability — hour grid". Implemented as designed.

- **Members tabs.** The Members card now has **Availability | Roster & invites**. Availability is the default when
  the board is visible; `?tab=roster` opens the roster, and membership actions and invite errors return there.
- **Grid.** One column per hour, one row per saved member. Free periods are blocks placed at their exact times:
  solid mint = one-off, striped with a blue outline = usual weekly hours (not confirmed). Grey = busy (the member has
  times that day, not at that hour). Hatched with "No data" = nothing saved. Night hours 00–06 are shaded.
- **Free together.** A row with how many members are free in each hour (free at any point in that hour), shaded by
  share of the group. The longest run with the most people free (two or more) is outlined and written out.
- **Now line** on today, in the board's time zone.
- **Hours toggle.** `00–24` or `12–24` (`?hours=12-24`); day, hours and tab are all kept in the URL.
- **Tooltips.** Blocks are focusable; hover or keyboard focus shows who, when and whether it's one-off or weekly.
  Each row also has a screen-reader summary ("Free: 18:00–22:00 (usual weekly hours, not confirmed)").
- **Phones.** Each member becomes a card with a full-width track (3-hour ticks) and the times written below.
  Only the day tabs scroll sideways.

Application: `GroupAvailabilityBoard` gains `FreeByHour` (24 counts) and `Now`. Same authorization and batched
reads as before. New test: `FreeTogetherCountsEachHourAndNowIsOnlyShownForToday`. 128 tests pass. 15 new
strings in Ukrainian and Russian. No storage migration.
