# Print timeline (api#343)

A print's timeline records what happened while it ran: when it started and finished, pauses, the
failure reason and claims. Code: `Models/PrintTimelineEvent.cs`, `Services/Timeline/PrintTimeline.cs`,
`Controllers/PrintTimelineController.cs`. Named "timeline" because `PrintEventService` already
means the notifier and webhook start/finish pipeline.

- **There is no Paused status, on purpose.** A paused print stays `Printing`; `Print.PausedSince`
  is set while a pause is open and `Print.PausedSeconds` sums the closed ones. Every `== Printing`
  check (finish matching, the bridge guard, claims, Spoolman usage, the sync snapshot's current
  print, Home Assistant) keeps working. Show "Paused" only while the status is Printing: a manual
  status edit does not clear `PausedSince`.
- **The timeline explains, it never decides.** Status, dates, duration and usage stay on the print.
  Manual edits (PUT, PATCH, bulk, status edit) write no entries: this is not an audit log.
- **Every rule is in `PrintTimeline`.** A pause is ignored on a print that isn't Printing, while
  one is open, or when its key is recorded; a resume needs an open pause and a time at or after
  its start; times before the print's start minus 5 minutes or after now plus 5 minutes are
  dropped; a print keeps at most 200 entries (`PrintTimeline_CapReached`). Keys are unique per
  print (`IX_PrintTimelineEvents_PrintId_Key`), which is what makes replays harmless.
- **Lifecycle entries ride on the transition's own save.** `PrintTimeline.StageStarted` is called
  where a print is created in Printing or a replay moves it there; `PrintTimeline.StageFinishAsync`
  before `PrintFinishTransition.SaveIfStillUnfinished` in each finish path, on that path's
  context, so a finish that loses its race drops its entries with its other tracked changes. Both
  are static for that reason. A new start or finish path must call them, next to the notifier
  call `docs/agents/webhooks.md` requires.
- **Finishing closes an open pause** at `endedAt`, else now.
- **Duration includes pauses on every printer**, so active time is `PrintTimeInSeconds -
  PausedSeconds`; accuracy analytics use it. printlog-bridge adds its measured pause time to
  Klipper's `print_duration` (which leaves out pauses and warm-up) for that reason. No firmware
  pause figure is taken: Klipper's `total_duration - print_duration` also counts warm-up.
- **Who sees what.** `pausedSince` goes wherever the status does (detail, summaries, feed, sync
  `isPaused`); `pausedSeconds` and `GET /api/Prints/{id}/timeline` are the creator's only.
- **Webhooks.** Moonraker `paused`/`resumed` apply the pause rules to the notifier's own open print
  of that file on that printer, keyed on receipt time, and go through the live-bridge drop like
  start and finish. OctoPrint's `Print Paused` stays unhandled on purpose: the Webhooks plugin
  has no resume event (`on_event` in its `__init__.py`), so a pause from it would stay open until
  the print ended. OctoPrint pauses come from the bridge's OctoPrint adapter.
