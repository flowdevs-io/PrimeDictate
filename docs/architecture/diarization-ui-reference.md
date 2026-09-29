# Reference: NVIDIA Nemotron 3 Diarization demo UI

Observed 2026-09-29 at `huggingface.co/spaces/nvidia/nemotron-diarization` (title "Live Speaker
Diarization"), by playing its built-in "NVIDIA" sample conversation (3 synthetic voices, about 58 s).
This describes layout only, as a design reference for the transcription workspace. No screenshot is
committed; the description below is from viewing the page.

## Overall layout

Dark, near-black theme with NVIDIA green as the accent. A top bar has the logo, the title, and tabs:
Conversation, Audio File, Live Mic, Multilingual (and possibly more off-screen). The Conversation tab
is two columns.

- **Left column (controls):** a "Ready to play" grid of sample cards (title plus "3 voices, 58s"), a
  text box to create a conversation from a topic, a speaker-count selector (2 to 8), Create and
  "Surprise me" buttons, then a green **Start conversation** button beside a **Stop** button. A card
  below shows the source and its blurb. These controls are demo-specific and not needed here.
- **Right column, upper: LIVE TRANSCRIPT panel.** A card with a small caps title, a status pill at the
  top right that changes ("Waiting", "Prepared", "Updating 23") and a green **Jump to latest** button
  that scrolls to the newest line while the list auto-scrolls.
- **Right column, lower: SPEAKER ACTIVITY panel.** A card with the elapsed audio length at the top right
  ("15.7s received", "57.6s planned"), a row of speaker chips, and one timeline row per speaker.

## Live transcript rows

Each turn is one row separated by a thin divider, with a fixed left gutter and a right-aligned time:

- **Left:** a small colored dot plus an uppercase label in the speaker's color, for example
  "SPEAKER 1" (green), "SPEAKER 2" (blue), "SPEAKER 3" (purple). The label is small and letter-spaced.
- **Middle:** the turn's text in large, light-weight white text, wrapping over several lines.
- **Right:** a muted time range, for example `00:00.6 - 00:09.8`.
- Rows appear as the audio streams in and later rows can extend (the last row's text was still growing,
  "Exactly. NVIDIA now describes itself as"). A new row starts on a speaker change, which also shows
  that the same speaker gets the same color each time (Speaker 1 came back in green).

## Speaker activity panel

- **Chips:** one rounded chip per speaker with a colored dot, the name in bold ("Nora", "Daniel",
  "Chloe") and a role in muted text ("host", "technology analyst", "company historian"). The chip of the
  currently detected speaker gets a bright outline in that speaker's color. In this demo the names and
  roles come from the synthetic script; a real run would show "Speaker 1..N" until renamed.
- **Timeline:** a horizontal track per speaker, labelled S1, S2, S3 in the speaker's color at the left.
  Each track is a dark rounded bar with colored segments where that speaker talks. Segments are
  positioned by time across a fixed width, so the tracks grow left to right as audio arrives (during the
  live run) and show the whole plan in advance before playback ("planned"). Overlapping speech shows as
  segments on two tracks at once.

## Colors and type

| Element | Look |
|---------|------|
| Page and cards | Near-black background (about `#0b0d0b`), slightly lighter card surfaces, 1 px subtle borders, rounded corners |
| Accent and speaker 1 | NVIDIA green, about `#76b900` |
| Speaker 2 | Sky blue, about `#3ea6ff` |
| Speaker 3 | Violet, about `#b35bd6` |
| Text | White for transcript, muted gray for times, roles and captions |
| Labels | Small uppercase, wide letter-spacing, in the speaker's color |

Speaker colors are stable per speaker across the transcript, chips and timeline. Colors are
approximations read from a screenshot, not from the page's CSS.

## Takeaways for PrimeDictate

- One row per speaker turn: colored label, large text, muted time range on the right.
- A per-speaker timeline track under or beside the transcript, using the same colors.
- Speaker chips that highlight who is talking now, with a place to rename.
- Auto-scroll with a "Jump to latest" control and a small status pill.
- For our data: live `completed` events carry `words[]` with a `speaker` id (1-based), so a row can be
  built per speaker change inside a final; delta events carry no speaker, so partial text has no label
  until its final arrives (see `nemotron-samples/`).

## What Justin wants on top of the demo (2026-09-29)

He pointed at the Speaker Activity panel (per-speaker bars, e.g. Nora green and Daniel blue, "92.5s
planned") and said that is roughly what he means, with two changes:

- **Vertical scroll:** the speaker rows should scroll down, so any number of speakers fits, and rows are
  added as new speakers are detected (no fixed count up front).
- **Horizontal scroll:** the timeline should scroll sideways as the conversation goes on, following the
  newest audio instead of squeezing the whole session into a fixed width.

The panel stays alongside the live transcript, and the session should be saved as a log (the app's
existing SQLite session store is the intended home).
