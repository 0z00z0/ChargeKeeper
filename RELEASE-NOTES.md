# ChargeKeeper release notes

The one source for what a release changed. The release workflow publishes the section for the tag it
is building as that release's body, and the application ships this same file and shows the running
version's section as its "What's new" report — so the two cannot say different things.

**One sentence per issue**, naming the issue number and what is better for someone using the
application, not what moved in the code. A change carrying no issue collapses into a single closing
line, or is left out. Newest version first; the heading is the version alone, exactly as it appears
in `ChargeKeeper.csproj`.

## 1.40.0

- #153 The power log now records every change to the lid-close delay length, from whichever surface
  made it, so the wait that is armed can be checked against the one that was configured.
- #154 A computer started with its lid already shut hands the lid-close action back to Windows until
  the lid next opens, instead of leaving the lid parked on "do nothing" with nothing serving it.
- #155 The lid-close battery target now says in the power log whether it armed and, where it did
  not, why — including the case where no battery reading has reached it at all.
- #157 A new "Sleep if the computer reaches a temperature" setting ends a lid-close wait early and
  sleeps the computer when it gets hot with the lid shut, which is what a laptop carried in a bag
  needs; the temperature is recorded alongside the battery history, and what happened is said at the
  next wake. Off by default, and unavailable on a computer that exposes no trustworthy reading.
- #158 The dashboard opens immediately rather than waiting on a reading from the vendor interface.

## 1.39.0

- #132 The third tray icon style is now called "Battery fill", which says what appears in the
  notification area rather than where the drawing came from.
- #133 The start and stop marks on the arc gauge reach past the ring, so the charge limit can be
  read at a glance in the notification area instead of disappearing into the ring at tray size.
- #135 Each tray icon now has an identity of its own, so the position chosen for it on the taskbar
  survives the application moving to another folder.
- #136 A new "Also show percentage" setting adds a second tray icon carrying the charge level as a
  number, and the number now fills the icon instead of sitting inside a margin.
- #137 A new "Show icons in main tray (experimental)" setting asks Windows to keep the icons on the
  taskbar rather than behind the overflow chevron, and puts things back as they were when switched
  off.
- #138 A "What's new" report shows after an update and stays reachable from the tray menu and the
  About page.

## 1.38.0

- The shared studio library moves to 0.7.0: the build now pins every package version in one place,
  and three tray colours plus the full/idle status glyph take their values from the studio palette
  rather than from hand-typed copies. The idle glyph changes colour slightly as a result.

## 1.37.1

- Earlier releases are described in their own entries on the releases page.
