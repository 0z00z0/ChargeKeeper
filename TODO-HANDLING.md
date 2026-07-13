# ToDo handling

How work is tracked in ChargeKeeper. Short version: **GitHub Issues are the source of truth; a
local git-ignored [`TODO.md`](TODO.md) mirrors them for at-a-glance/offline work; the two are kept
in sync both ways.**

## Two surfaces

| Surface | Role | Committed? |
|---|---|---|
| [GitHub Issues](https://github.com/0z00z0/ChargeKeeper/issues) | **Source of truth.** Every real task is an issue, labelled per the taxonomy below. | n/a (GitHub) |
| [`TODO.md`](TODO.md) | **Local mirror** — a grouped, tagged snapshot for quick scanning and offline planning. | No — git-ignored, local-only. |
| `TODO-HANDLING.md` (this file) | The convention itself. | Yes. |

When the two disagree, **GitHub wins** — `TODO.md` is regenerated/corrected from it.

## The "always sync" rule

`TODO.md` and the issue tracker must never drift. In practice:

- **New work** → create a GitHub issue first (labelled), then add a line to `TODO.md`. Don't put a
  task only in `TODO.md`.
- **Status change** (start / finish / block / descope) → update GitHub (labels and/or open↔closed)
  **and** move the line to the matching `TODO.md` section, in the same session.
- **Done** → close the issue with a comment referencing the implementing commit(s); move the line to
  `TODO.md` → _Recently done_.
- **Descoped / won't-do** → close with a comment saying why (e.g. crosshair #8: pan/zoom descoped).
- Refresh the `_Last synced_` date in `TODO.md` whenever you touch it.

An automated assistant working in this repo keeps both surfaces in sync as part of any task that
changes work status — it is not a manual afterthought.

## Label taxonomy

Every open issue carries **one `type`**, **one or more `area:`**, and optionally a **status**.

**Type** (what kind of work)
- `enhancement` — new user-facing capability.
- `bug` — defect / regression.
- `refactor` — internal cleanup / restructuring, no behaviour change.
- `performance` — efficiency / hot-path work.
- `ci` — CI, build, release, packaging.
- `documentation` — docs only.

**Area** (what part of the app)
- `area:tray` · `area:settings` · `area:graph` · `area:mqtt` · `area:vendor` · `area:network` ·
  `area:core` · `area:installer`.

**Status** (optional)
- `blocked` — waiting on an external dependency or an explicit go-ahead.
- `idea` — brainstorm backlog, not committed/scheduled work. Removed once an item is scheduled.

## `TODO.md` format

Grouped by working status, one line per issue:

```
- [ ] **#<issue>** Title — `type` `area:*` `status` — optional one-line note
```

Sections, in order: **In progress · Scheduled / ready · Blocked · Backlog (ideas) · Recently done**.
Closed issues stay briefly under _Recently done_ for context, then age out.

## Commit convention

One commit per issue where practical, message tagged `Item #<n>` / referencing the issue, so history
maps to the tracker. See the repo's commit history for examples.
