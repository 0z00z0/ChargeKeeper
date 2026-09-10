# ChargeKeeper release notes

The one source for what a release changed. The release workflow publishes the section for the tag it
is building as that release's body, and the application ships this same file and shows the running
version's section as its "What's new" report — so the two cannot say different things.

**One sentence per issue**, naming the issue number and what is better for someone using the
application, not what moved in the code. A change carrying no issue collapses into a single closing
line, or is left out. Newest version first; the heading is the version alone, exactly as it appears
in `ChargeKeeper.csproj`.

## 1.48.0

- #176 Settings are held in the shared library's sectioned settings store. A change that cannot be
  written to disk says so, rather than looking saved and being gone at the next start; an existing
  settings file is read and kept exactly as it stands, with every value carried across; and a file
  that will not parse, or that a build reading more than this one wrote, is refused rather than
  quietly replaced by defaults.

## 1.47.1

- Building the installer here now stops when the application or the installer cannot be signed with
  a timestamp, rather than packing a file whose signature stops verifying the day the certificate
  expires.

## 1.47.0

- The shared components move to their current release: a fault that could end the application while
  a setting was being changed, or as it exits, is closed, and a build made here can no longer sign
  the application or its installer without a timestamp, which would otherwise leave the signature
  unverifiable the day the certificate expires.

## 1.46.0

- #172 The Keep Awake page now says, in the same words as the Lid delay page, that a running session
  stops a lid close from sleeping the computer.
- #174 The Keep Awake page now also says when Windows will sleep the computer once a session ends and
  nothing else is holding it awake: after a stated period of no use, on mains or on battery, given as
  a floor rather than a countdown, since another application's own hold can push the moment out.
- #173 A lid-close sleep held back by a running keep-awake session is no longer lost. It is served the
  moment the session ends, if the lid is still shut, instead of leaving the computer awake with the
  lid closed until Windows' own idle timeout — five hours on mains, measured on one machine.
- #170 On a computer that sleeps by Modern Standby, the Lid delay page now says that Windows can enter
  standby on its own idle rules while a wait runs. This states the gap rather than closing it: such a
  machine can still fall asleep mid-wait, and the issue stays open.
- #175 The log now records what the lid switch actually reported and how long since the keyboard or
  mouse was last touched, so a lid close logged while someone is plainly still typing can be told
  apart from a real one. Two new Home Assistant readings show the last such event and when it arrived,
  joining five more that report what the application itself is doing: what last changed and when,
  what the lid-close wait is currently on, and countdowns for the lid sleep and the keep-awake hold,
  the two countdowns sorted into the main Sensors section alongside the battery readings. Battery
  state, battery health and battery power state are now closed lists of words rather than free text,
  and the network-profile reading says "No profile matched" instead of a word that also meant no
  reading had arrived.

## 1.45.0

- #170 The power log now says what a lid-close wait actually did. It records whether the computer
  slept while the wait was running and for how long, so a wait that was interrupted can no longer
  look like one that held; what kind of sleep the computer does at all; whether Windows accepted the
  request to stay awake, for both the lid delay and Keep Awake; and which conditions the wait armed
  with, saying an unset condition as plainly as a set one. Nothing behaves differently — this is
  what the log says, so the fault it exposes can be measured rather than guessed at.

## 1.44.0

- #168 A lid-close wait armed only on a battery target no longer suspends the computer the moment
  a charger is connected. A new "Switch off when a charger is connected" setting, on by default,
  ends the wait instead, and a notification confirms the computer stayed awake because the target
  can no longer be reached.

## 1.43.0

- #140 The Lid delay badge on the dashboard now names the lock-at-close setting, and its
  explanation covers the battery target as well as the timer.
- #134 The tray menu's Settings submenu now picks the icon style directly, with the active style
  marked, instead of requiring a trip to the Settings window.
- #139 The installer's "still running" page now offers Retry alongside Cancel, so closing the
  application from its notification-area icon does not mean starting the install over.
- #149 Choosing Update from the tray menu now installs without the setup wizard appearing, and a
  failed update is reported at the next start instead of passing silently.
- #159 A Smart Charge threshold write now records its outcome in the log, so a preset that does
  not take effect can be diagnosed.
- #152 The first strings move to a translatable resource file, a pilot covering one window.
- #161 "Check for updates" is now on the About page as well as the tray menu.

## 1.42.1

- #163 Appearance now holds every visual setting — the tray percentage icon and the three history-
  graph controls join it from General. App diagnostics gains "Open settings file" and an "Open log"
  menu (app.log, power.log, performance-history.csv) alongside the settings-folder actions moved
  there from General, so a config or log file is one click away instead of a trip to the file
  system.

## 1.41.0

- #160 The settings file now spells its section names the same way as everything inside them, so
  reading it means one convention instead of two. A settings file written by 1.28.0 or later is
  not carried across: the first start on this version comes up on defaults and keeps the previous
  file beside it as `settings.json.pre-grouping-backup-<date>`, to copy values back from by hand.

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
