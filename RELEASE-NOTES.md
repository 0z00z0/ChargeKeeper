# ChargeKeeper release notes

The one source for what a release changed. The release workflow publishes the section for the tag it
is building as that release's body, and the application ships this same file and shows the running
version's section as its "What's new" report — so the two cannot say different things.

**One sentence per issue**, naming the issue number and what is better for someone using the
application, not what moved in the code. A change carrying no issue collapses into a single closing
line, or is left out. Newest version first; the heading is the version alone, exactly as it appears
in `ChargeKeeper.csproj`.

## 1.60.0

- #226 The What's new window now opens tall enough to show the whole report, with no scroll bar;
  before, it opened at its smallest height with the text cut off.
- #232 Checking for and installing an update now runs on the studio's shared update components.
  Nothing changes in use: the same button, the same wording on it, the same offer to install, and
  the same rule about which check may interrupt with a dialog and which may not.

## 1.59.1

- #225 The About window now opens tall enough to show the whole card and Check for updates under
  it, with no scroll bar; before, it opened at its smallest height with the button cut off.

## 1.59.0

- #223 ChargeKeeper now takes its generic building blocks — the Settings info bubbles and section
  headers, the network-name prompt, the title bar, message boxes, crash logging, the tray icon's
  size and file format, and the MQTT module — from the studio's shared library; nothing changes in
  use, and an error in background work that nothing waited for is now written to the log instead of
  passing unrecorded.

## 1.58.4

- #212 A script bound to the lid now runs on every lid change, whether or not Lid delay is
  switched on.
- #214 The battery temperature reading appears as soon as it is available, rather than only
  after the first 20-second history update.
- #170 On battery, a lid-close wait now runs its full length: while it runs, ChargeKeeper sets the
  Windows battery sleep timeout to Never and puts the previous value back when the wait ends, or at
  the next start if the app was stopped first. Before, Windows slept the computer about fifteen
  minutes after the lid closed, whatever the delay said.
- #217 There is one log, app.log: the power, lid and sleep lines sit in it in time order with
  everything else, and power.log is no longer written.
- #218 With no network, MQTT no longer fills the log with an error for every entity: losing the
  broker and getting it back are one line each, and the current state is sent again on reconnect.
- #219 A script that fails with a PowerShell error is now said to have failed, with the error in
  plain words, and raises the script-failed notification; a run that worked says so.
- #220 The log records how long the dashboard took to open after a tray click, and whether its
  window was newly built or reused.
- #221 Settings help text is shorter throughout, stating what a setting does rather than why.
- #222 Run now on the Scripts page now always runs the script as currently typed, rather than
  sometimes running the last saved version of an edit made just before the click.

## 1.58.3

- #208 The dashboard's Lid delay status line, with both the delay and battery-level conditions on,
  no longer wraps raggedly or gets cut short — each part of the sentence has its own line.
- #209 The About window opens tall enough to show Check for updates under the card without
  scrolling, and the Check for updates button on the Settings About page now sits centred under the
  card instead of flush left.
- #210 The battery temperature reading on the Settings sleep card and the lid-close ceiling no
  longer waits for the reading to vary before trusting it, so a genuinely steady reading is no
  longer withheld for minutes at a time.
- #211 The About card's text is larger — the whole card is about a fifth bigger — and the frameless
  About window is wider to show it without scrolling (brand component 0.9.2).

## 1.58.2

- #206 Clicking anywhere inside a dashboard feature box — its title, its description or its
  collapsed row — no longer switches it on or off. Only the switch does that, and only the chevron
  expands or collapses the box.
- #207 The Smart Charge preset buttons and "Charge to 100 % once" now match the look of the rest of
  the dashboard's buttons, and mark the preset in use the same way the Lid delay chips mark a
  selected one.

## 1.58.1

- #205 The "Sleep if the computer reaches a temperature" card, and the temperature ceiling on a lid
  close, now go by whether a trustworthy reading has ever arrived since ChargeKeeper started, rather
  than the reading of the moment. Under steady load the card no longer disables itself, with the
  wrong explanation, for minutes at a time, and a lid closed during such a stretch still gets its
  temperature ceiling.

## 1.58.0

- #204 The About window and the Settings About page share one layout: Check for updates sits on its
  own under the About card on both, and says what it found on the button itself — Checking, then Up
  to date, or Update to the version available, which opens the update dialog when selected — while a
  failed check still explains itself in a dialog. It also checks by itself each time the About window
  is shown or the Settings window is opened, showing the result on the button only, and two windows
  open at once share one check.
- #192 What's new is a button inside the About card, beside Website and Donate, on both the About
  window and the Settings About page, and opens ChargeKeeper's own report; the separate button below
  the card is gone, and the card's buttons now take their natural widths and wrap when there is not
  room for them in one row (brand component 0.9.1).
- #180 The studio typeface now reaches an installed copy. The 1.52.0 entry for #180 said the About box
  shows its text in the studio typeface; that held only for a copy built from source, and an installed
  copy fell back to a system font until this release, which also refuses to build an installer that
  lacks the font file.
- #194 The Percentage digit style setting is available whenever the tray draws digits: with the
  Numeric % icon style, or with the second percentage icon switched on.
- #195 The Clock cells digit style is now called Staggered.
- #196 The digit style can be chosen from the tray's right-click menu: from the main icon while it
  draws Numeric %, and from the second percentage icon's own menu.
- #197 What's new is no longer in the tray's right-click menu; it stays on the About window and the
  Settings About page.
- #198 A dashboard graph that is hidden is no longer built or drawn, and the pop-out graph draws only
  the graph on display.
- #199 Hovering the dashboard, the pop-out graph or the About window no longer shows a tooltip reading
  Esc.
- #200 The About window opens without a title bar and closes when it loses focus or Escape is pressed,
  as the pop-out graph does.
- #201 The dashboard's REMAINING line no longer starts with a tilde, and is hidden while there is
  nothing to estimate instead of showing a dash.
- #202 The battery graph legend says Level rather than SoC, Level, Limit and Power each explain their
  line on hover, and the dashboard's other hover texts are reworded.
- #203 The Notifications page has a switch for each of the eight notifications and one sound for all
  of them: silent by default, the Windows sound, or one of three sounds that ship with ChargeKeeper and
  stay quiet while Windows is holding notifications back.

## 1.57.0

- #193 The Numeric % tray icon can be drawn in three digit styles, chosen on the Appearance page
  beside the tray icon style: the one it has always had, a heavier face that runs past all four
  edges, and one after a smart clock's display, where each digit is cut by its own cell with a seam
  between them and the cells sit at slightly different heights. All three now draw 100 as tall as
  any other reading, in a condensed face, instead of shrinking it to fit.
- This release also carries everything listed under 1.54.0 (#168, #169), 1.55.0 (#144, #143, #167,
  #191) and 1.56.0 (#165, #166) below, since those versions were never published.

## 1.56.0

- #165 Measuring itself now costs ChargeKeeper almost nothing: the once-a-second reading of memory
  and handles no longer takes a snapshot of every process on the computer, which on this machine had
  grown to 12.5 ms and a large share of what the application itself was using, and is now under four
  microseconds. Thread count is the one figure with no cheap route, so it is re-read once a minute
  and the last count stands in between, and the history file gains how much the application has read
  from and written to disk.
- #166 The performance graph draws the processor line's two-minute average as a dashed line beside
  it. Windows accounts processor time in steps of 15.625 ms, so at the faster sampling rates a single
  sample is either nothing or a spike many times what ChargeKeeper costs; the average is the line to
  read for the real figure.

## 1.55.0

- #144 Every saved list on the Settings pages now works the same way: network profiles gain the
  Activate button and the in-use marking the other lists had, the profile matching the network this
  computer is on is the one marked, and a list stops marking anything once its own feature is
  switched off while keeping the choice for when it is switched back on.
- #143 Switching network profiles off releases the keep-awake hold a profile was keeping, whichever
  way it was switched — the Settings page or Home Assistant — and a "keep awake here" tick that
  cannot act while profiles are off now says so on its row, with the switch that decides it repeated
  on the Keep Awake page.
- #167 Switching network profiles on applies the profile for the network this computer is already on
  straight away, instead of leaving it until the next dock, roam or restart.
- #191 A script can run when this computer joins or leaves a named network profile: the script stays
  with its profile through a rename, a brief drop in the network while docking runs nothing, and
  starting the application runs nothing at all.

## 1.54.0

- #168 Connecting a charger while the lid is shut and a battery target is waiting no longer puts the
  computer to sleep: the target pauses, the computer stays awake while it charges, and the countdown
  towards the target carries on once the charger is removed, whether or not a keep-awake session is
  running.
- #169 Closing the lid on a computer that is already charging, with a battery target set, holds it
  awake while it charges instead of sleeping it at once, and the countdown starts when the charger is
  removed; the "Switch off when a charger is connected" setting and its Home Assistant switch are gone,
  since a charger no longer ends a wait.

## 1.53.0

- #187 Logs and history files sit in their own Logs and History folders inside the data folder, so
  settings are what is found at the top; files an earlier version left there move into place by
  themselves at the next start, and one that cannot be moved stays where it is rather than being lost.
- #188 The Settings window no longer grows towards the full height of the display: the first time it
  opens it fits the tallest page, up to four fifths of the screen, and after that it opens at the size
  it was left at.
- #189 The App diagnostics page drops the performance log's own Show in Explorer button, since the Logs
  card already opens that file and now says how long it is kept, and the settings-file text sits above
  its buttons instead of being squeezed beside them.
- #190 The Settings pages run General, Appearance, Smart Charge, Keep Awake, Lid delay, Notifications,
  Scripts, MQTT, App diagnostics, About, and Appearance gathers the tray icon style, the main-tray
  option and the downtime gap threshold from General under Tray, Dashboard and Graph headings.
- #185 The Off word beside a folded dashboard card's title sits on the title's line rather than above
  it.
- #77 A build made on a developer's own machine is signed without asking a timestamp server, so it no
  longer fails when that server cannot be reached; published installers are still signed with a
  timestamp.

## 1.52.0

- #186 A script of more than one line opens with all of its lines in the box instead of only the
  first, so reopening the Settings window no longer hides the rest of a script — or loses them the
  next time that script is edited.
- #180 The About box shows its text in the studio typeface rather than falling back to a system
  font, closes on its own when it loses focus, and opens tall enough to reach the list of libraries
  without scrolling.

## 1.51.0

- #183 The duration buttons on Keep Awake, and the delay and battery rows on Lid delay, are drawn
  again and stay drawn: once "One line until it matters" had folded a card away, those rows stayed
  missing for the rest of the session even after the feature was switched back on and after the
  setting itself was turned off again — and they now carry the same look as the range buttons above
  the history graph, as every row of quick buttons in the application does.
- #184 The screen-hold chip in the Keep Awake line sits on the line rather than riding above it, and
  takes an outline and a pointer-over highlight so it reads as something to press instead of as a
  label.
- #181 A new Appearance setting leaves the history graph out of the dashboard popup and puts a single
  button in its place that opens the larger graph window; the feature cards below keep the spacing
  they already have.
- #182 The Scripts page shows a script as plain text only: the coloured reading beneath the editing
  box is gone, and the component that drew it leaves the installation with it.

## 1.50.0

- #178 A Scripts page runs a named PowerShell script when the charger goes in or comes out, or when
  the lid closes or opens — one script per event and direction, typed into a plain box with a
  coloured reading of it beneath, tried straight away with Run now, held to one run at a time and a
  60-second limit so a stuck script cannot pile up, with everything it prints kept in the application
  log and one notification the first time it fails rather than one every time; the page states that a
  lid script only runs while Lid delay is switched on, and that a script — and any program it starts
  — runs with administrator rights.

## 1.49.0

- The Windows interface components carried inside the installation move up two releases, so the
  application's windows, controls and text rendering take the fixes made across that span, and
  nothing has to be added to the machine for it.

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
