# Meeting v1 acceptance test

One real call on Justin's Windows PC, run by the local session with Justin present. Every item is pass or
fail; write the result and the evidence (session id, screenshot, or a sessions.db query) next to it.
Meeting v1 passes when every item passes. Any failure becomes a bug with the session id attached.

## Setup

- [ ] Build under test recorded (commit id) and `dotnet test` result recorded.
- [ ] Nemotron worker files in `%LocalAppData%\PrimeDictate\models\nemotron` (`cuda\nemo-speech.exe`, ASR and diarizer GGUFs).
- [ ] C: drive has at least 10 GB free. Database and media folder backed up first.
- [ ] Real call: 10 minutes or more, at least 2 remote people, Justin talking, on speakers and again (2 minutes) on headphones.
- [ ] Source: "Microphone + system audio", model "Nemotron 3.5 (GPU, CPU if it fails), speakers", language English (US).

## Checklist

1. **Backend.** The badge reads "Nemotron on cuda:0", the run record says `EffectiveBackend: cuda:0`, and no fallback-to-CPU or "mixed stream" note appears. PASS/FAIL
2. **Live view order.** Rows are in start-time order, none appear twice, and no row disappears and returns. Check live and after Stop. PASS/FAIL
3. **Stored data.** For the session in `sessions.db`: every segment id is unique, and `SELECT id FROM segments WHERE session_id = ? AND result_version = <active> ORDER BY start_ticks, id` equals the order shown. (Rows are stored in the order the two streams finish, so the raw table order is not expected to be sorted; loading, the screen and exports all sort by start.) PASS/FAIL
4. **You versus remote.** Everything Justin says is "You"; remote voices are never "You". Spot-check 10 rows against what was said. PASS/FAIL
5. **Echo, judged separately.** Speakers run: no remote sentence appears again as "You" within 5 s. Headphones run: none at all. Record the repeat count for each run. PASS/FAIL
6. **Talk-over.** In a passage where Justin and a remote person overlap, both are transcribed in separate rows whose times overlap. PASS/FAIL
7. **Times.** Each row shows start and end; the end is after the start and within the recording length; no row is shorter than 0.5 s unless it is a real one-word answer. PASS/FAIL
8. **Speaker stability.** Each remote person keeps one speaker number for the whole call (allow at most one extra number per person). Record how many remote numbers appeared against how many people spoke. PASS/FAIL
9. **Merge and rename.** Merge any extra numbers into the right person; rename one speaker; lanes, rows and counts update at once. PASS/FAIL
10. **Reopen.** Close the app, reopen, open the session: same rows, same order, renamed and merged speakers kept, timeline intact. PASS/FAIL
11. **Export.** TXT, SRT and JSON exports open in another tool; each has the same rows, order, speaker names and times as the screen. PASS/FAIL
12. **Audio file.** The saved stereo WAV has the mic on the left and system audio on the right, and its length matches the last row within 2 s. PASS/FAIL
13. **Clock check.** Play a click or clap on the video while clapping into the mic; the two streams' times for the same event differ by less than 0.3 s. PASS/FAIL
14. **Stability and load.** No crash and no entry in `logs\desktop-crash.log`; no "behind" warning longer than 5 s; GPU memory under 6 GB; overall CPU under 60 % and no dropped audio (no gap notes). Record the numbers. PASS/FAIL
15. **Long silence.** Mute both sides for 2 minutes, then speak on each side: both are transcribed within 5 s and no wedge or restart notice is left unexplained (upstream NeMo-Speech.cpp issue 48). PASS/FAIL
16. **CUDA fallback.** Once on purpose, launch the app with the CUDA `bin\x64` folder removed from `PATH` and `CUDA_PATH` unset (or with `PRIMEDICTATE_NEMO_DEVICE=cuda:0` and no cuda folder): the app shows a visible message, falls back to CPU, still transcribes, and the badge says CPU. Nothing fails silently. PASS/FAIL
17. **Stop and delete.** Stop while a remote person is speaking keeps everything spoken so far; deleting a finished session works and does not crash. PASS/FAIL

## Result

Build, date, tester, and a one-line verdict. List failures with session ids.
