# /fship list from RynthChat never left two clients; tests that never ran; a test-gated install build (2026-10-30)

## The cause (flight recorders, /fship list sent to all five clients through dispatch.txt)
- Three clients sent the Talk and got ILT's reply: a dashed line, "[FSHIP]: Fellowships in your
  area:", one line per fellowship, and another dashed line. None of RynthChat's standard filters
  matches those lines (ChatFilterTests FshipListNotFiltered).
- Two clients (Silentstorm, and the client with RynthChat popped out) sent nothing, but the
  dispatch said ok. RynthCore sends a /command through AC's outgoing-chat function, using the
  chat manager AC gave the last line typed in its chat bar. Until a line goes through the chat bar
  in a login, RynthCore types the line into AC's window with simulated keys. In a background
  window, or with RynthChat's window popped out, those keys were lost.

## The fix (RynthCore engine)
- With no chat target captured, a server command now goes to the server as "@command" through
  Event_Talk, which needs no target. ACE runs "@fship list" exactly like "/fship list".
- AC's own commands (/loc, /framerate, /logout, ...) and chat (/f, /a, /tell, /say, the channels)
  still use the chat bar, as do lines with non-ASCII characters (ServerCommandFallback).
- The log names the path taken for the first five lines that went either way.
- When a plugin takes a typed or dispatched chat line, the log says which plugin. It logs three
  lines per plugin and command word.

## Tests that never ran
- RynthAiHostTests: RingEscapeTests and RingEvadeTests had been registered again earlier today;
  PetDamageTintTests had never been registered. It is now.
- scripts/Run-AllTests.ps1 now checks every test program for test groups that exist but never run:
  a class with a static Register(Runner) that nothing registers, or a static RunXxx() that nothing
  calls. Any it finds fail the run as "<program> (registration)".

## Rynth-full-install-test-build.cmd (Desktop)
- Checks that the three repos are on the test branch.
- Runs every test program (Run-AllTests.ps1, all three repos) and stops on any failure, with the
  summary and logs.
- Then builds the installer, installs it silently, and copies the experimental plugins over.
