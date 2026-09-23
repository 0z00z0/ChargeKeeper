<!-- lang: en-GB -->
# ChargeKeeper on Linux — plan

Status: plan only, nothing built (2026-09-23). The measurement and reasoning behind every decision
below is `LINUX-PORT-ASSESSMENT.md`; this file carries only the way forward. Scoped against
ChargeKeeper after the FocusDesk split, so the focus session, its levers and the Screen page are
out of it.

## What the first pass is

A headless daemon that holds the charge limit, the lid-close wait, keep awake, the triggers and the
Home Assistant surface, plus a tray and settings front end over it.

**Do not lead with the charge limit.** KDE Plasma ships one in Power Management and GNOME ships a
battery-health setting in its Power panel. What neither offers, and what this first pass is scoped
to, is the presets and their cascade, the travel override, the lid-close wait against a charge
target or a temperature ceiling, keep awake with its own presets and countdown, scripts bound to
lid, network and power triggers, and sixty-odd Home Assistant entities with commands.

## The three machines

| Machine | Desktop | Distribution | Tray hosting | Delivery | Threshold attributes expected |
|---|---|---|---|---|---|
| Lenovo X1 Yoga 4 | COSMIC | Pop!_OS | panel applet | `.deb` | Yes — the documented case |
| HP 840 G3 | KDE Plasma | Fedora | native | RPM | No — documented absence |
| Surface Pro 4 | GNOME | Fedora | extension the person installs | RPM | **Unknown — the least certain of the three** |

The Surface is the one the assessment could not resolve from documentation at all. It may have no
threshold support, in which case it keeps every capability except the charge limit and its presets,
and its battery page is read-only.

## Decided constraints

**Project shape.** The same repository, `0z00z0/ChargeKeeper`, with new targets: a platform-neutral
`ChargeKeeper.Core`, a `ChargeKeeper.Platform.Linux`, an Avalonia head and a privileged helper,
beside the existing WinUI head. A second repository would fork four fifths of the service layer
into two copies that drift.

**Interface framework.** Avalonia, X11 backend, on the shipping default rather than the
experimental Wayland one. The tray is a StatusNotifierItem published on the session bus, carrying
the icon as a pixmap; the pixmap is drawn by the ported `Helpers\IconGenerator` drawing code, which
already produces the arc, the numeric face and the three digit styles. The existing `UI\` project
is a **reference to port against, not code to reuse** — every window is WinUI markup, and what
carries over is the layout decisions, the strings and the control arrangement, restated in Avalonia
markup.

**Privilege boundary.** A polkit-authorised system helper, `chargekeeper-helper`, owning
`xyz.z00z.ChargeKeeper1` on the system bus, the only thing that writes a sysfs attribute; the front
end and the daemon never write one. A udev rule loosening the attribute's group would be one file
instead of a program, at the cost of granting every process running as that user permanent
unconditional write with no per-action check and no record of who asked.
**Verify on the machine before relying on it:** whether polkit's shipped rules let an active local
session authorise without a password each time, and whether the desktop's own charge-limit control
holds the same attribute at the same time.

**Delivery.** Native packages built from one source tree by CI — an RPM in a COPR repository for
both Fedora machines, a `.deb` in a signed flat repository published from the same run for Pop!_OS.
**No Flatpak.** A Flatpak would be one build installing from the software store on all three
desktops, which is the simpler install; but a sandbox owns no system bus name and writes no sysfs,
so the helper, its polkit action and its system unit would still have to arrive by a second,
non-Flatpak step on every machine. The two-step install costs more than the single build saves.

**Installation target.** One command to add the repository and one to install, once per machine,
with the helper, the polkit action, the system unit, the user unit and the front end arriving
together. Afterwards `dnf` and `apt` carry every update with nothing to click. The in-application
update flow, the download window, the handover record, the installer, the scheduled task and the
elevation model are **deleted, not ported**.

**Nothing writes a sysfs attribute on any machine until step 1 has answered on that machine.**

## Generic over specific

Every area takes the most general mechanism that covers the need. The vendor-specific surface is
confined to one thing: whether the threshold attribute exists.

| Area | Mechanism | Rejected as too specific |
|---|---|---|
| Charge limit | The power supply class's `charge_control_start_threshold`, `charge_control_end_threshold` and `charge_behaviour`, by attribute name | A driver-aware path per machine. No vendor driver is named anywhere in the code; a machine that does not expose the attribute simply has no charge limit |
| Battery, adapter, health | The same power supply class, read from sysfs | UPower on the session bus — a dependency absent from a headless run, reading the same attributes |
| Temperature | hwmon, falling back to the thermal zone class | A sensor name table keyed on the firmware's machine identity |
| Lid, sleep, keep awake | systemd-logind: a block inhibitor over the lid switch, sleep and idle, and the prepare-for-sleep signal | Any desktop's own power-management bus name |
| Keeping the display on | The XDG desktop portal's inhibit interface | The KDE and GNOME screensaver bus names |
| What is holding the machine awake | logind's own inhibitor list, read over the bus | A child process parsed for its text, as Windows requires |
| Network location | NetworkManager's bus, a connection held by UUID | A distribution's network configuration files |
| Scripts | The interpreter the script's own first line names, defaulting to `/bin/sh` | Assuming bash, or any one shell |
| Tray | StatusNotifierItem over D-Bus, the icon as a pixmap | A themed icon name per desktop, and anything KDE-only or GNOME-only |
| Sleep accounting | `/sys/power/suspend_stats` with journal timestamps and battery deltas | A desktop's own power log |

## Steps

### The four questions the machines settle — first work, on each of the three

1. **Do the charge-threshold attributes exist?** On each machine, list the power supply class's
   battery directory; record which of the three attributes are present, what they read, what a
   write is rounded to, and the kernel version. **Gates** the charge limit, the presets, the
   travel override, the lid-close discharge target and the Smart Charge entities, per machine. A
   negative answer drops that machine to a read-only battery page and leaves every other
   capability intact.
2. **Does the desktop's own lid and idle handling conflict with an inhibitor taken from outside
   the session?** With the session running, take a logind block inhibitor over the lid switch,
   sleep and idle from a process outside it, close the lid, and record whether the machine stays
   awake and whether the desktop acts anyway. Repeat for the display-idle inhibit through the
   portal. **Gates** the lid-close wait and keep awake — the two capabilities with the largest
   reuse and no other dependency.
3. **Is the adapter's rated wattage readable?** Read every attribute of the mains supply and of the
   Type-C port class on each machine. **Gates** one reading, the adapter entity and the charger
   card. A negative answer on all three deletes them rather than faking a value.
4. **How does a drawn tray icon behave?** Publish a StatusNotifierItem carrying a drawn pixmap;
   record how it renders at each panel size and scale the desktop offers, what a redraw per percent
   costs, and what happens when the panel or the watcher restarts. **Gates** the tray and the front
   end's shape, per desktop.

Steps 1 to 4 run per machine and are independent of each other. Answers are recorded in
`LINUX-PORT-ASSESSMENT.md` as measurements, replacing its unestablished entries.

### Work that needs no machine — starts immediately, in parallel with the above

5. **Extract `ChargeKeeper.Core`** inside the existing repository: the platform-neutral service
   code, the policies, the settings shape, the entity catalogue and naming rules, the icon drawing
   and the tests that already run against fakes. The Windows head keeps building against it
   unchanged, and its suite passing is the proof the extraction is faithful. Twenty-two service
   files name a Windows-only interface and stay behind the platform boundary; the rest move.
6. **Widen the vendor abstraction into the platform boundary.** `Vendors\Abstractions` already
   carries the threshold, charger and standby contracts, including the split between numeric
   thresholds and discrete modes the kernel's rounding behaviour needs. Add the lid, inhibitor,
   thermal, network-location and script-run contracts beside them, with a fake implementation of
   each.
7. **Port what sits on a Windows-only settings store or logging component.** The sectioned settings
   store, the watch, the diagnostics, the MQTT client and the discovery layer are already
   platform-neutral packages — measured: only the four WinUI-flavoured packages carry a Windows
   target framework. **Porting such a file therefore means changing its platform call, not its
   store or its log**: NLog runs unchanged, the settings document keeps its shape, its section
   spellings and both version keys, and the data folder moves from the Windows application-data
   path to the XDG state directory in `AppPaths` alone.
8. **Write the sysfs module** against a fake filesystem root — one module replacing the three
   vendor modules and the native bridge. Read, write, rounding read-back, and the absent-attribute
   case that the HP is expected to be.
9. **Write the privileged helper and its polkit action**, against the same fake root. The helper
   exposes reading and writing a threshold and nothing else.
10. **Rebuild sleep accounting** from `/sys/power/suspend_stats`, journal timestamps and battery
    deltas. The wake reason is an interrupt name, not a sentence; every surface says what it could
    not read rather than inventing wording, exactly as the awake-hold reader already does.
11. **Supply the MQTT wiring the WinUI package provides.** The client and the discovery layer are
    platform-neutral; what is Windows-only is the hosting flavour around them.
12. **Build the Avalonia shell**: the tray item and its menu, the Settings window and its pages, the
    charge card. Ported against the WinUI markup as a reference.
13. **Write the packaging**: the RPM spec, the Debian control files, the systemd system unit for the
    helper and user unit for the front end, and the CI job that produces both from one tree. The
    user unit restarting on failure replaces the scheduled task, the single-instance lock and the
    relaunch record.

### What the gates unlock — on the machines

14. **Charge limit on every machine step 1 answered yes for.** Presets, the cascade, the travel
    override and the Smart Charge entities follow.
15. **Lid-close wait and keep awake**, once step 2 shows the inhibitor holds. The Windows
    battery-sleep park has no counterpart and is not ported: a logind block inhibitor has no time
    limit.
16. **Scripts on triggers and the network location**, from the logind signals and NetworkManager's
    bus. A script inherits the rights of the front end, which is unprivileged — a behaviour change
    from Windows, stated on the page.
17. **The tray and the front end, KDE first**, because KDE hosts a status-notifier item with
    nothing installed. GNOME follows, once the person has the extension; COSMIC last, because its
    panel applet owns the watcher name and is documented from project sources rather than a
    specification. The daemon ships to all three at once — nothing in it is desktop-specific.
18. **Publish the packages** and install by the one-command route on each machine.

## Verification

- Step 5: the Windows head's existing suite passes unchanged against the extracted core.
- Steps 8 and 9: the fake-root tests cover a write rounded to a value the hardware supports and an
  absent attribute, and the helper refuses an unauthorised caller.
- Step 14: a threshold written through the helper reads back from sysfs at the value the hardware
  accepted, on each machine that has the attribute.
- Step 15: the lid closes with the inhibitor held and the machine is still awake past the point the
  desktop would have suspended it.
- Step 17: the tray icon is looked at on each desktop at each panel size, not inferred from a
  passing build.
- Step 18: a fresh machine reaches a running application by the stated commands and nothing else.

## Not in the first pass

- **Parity with the Windows application.** The dashboard, the battery-history graphs and their
  pop-out window, the performance sampler, the notification sounds, the What's new window and the
  About card's sizing work all wait.
- **The in-application update flow**, the download window, the handover record and the refusal
  wording — the distribution updates the application.
- **A per-program account of what kept the machine up**, and a wake reason rendered as a sentence.
  Linux has neither.
- **The adapter's rated wattage**, unless step 3 answers yes on some machine.
- **The brand-mark tray style**, which needs a non-Windows brand component.
- **The Hyper-V uplink reading**, which has no counterpart.
- **A Flatpak**, and any desktop-specific code path beyond the tray hosting difference.
