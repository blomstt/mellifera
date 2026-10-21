# Changelog

Every patch gets its own version and an entry here: what went wrong in practice, then what changed.

## 0.2.0 (2026-10-21)

First version of this repository. A fresh install is an empty hive and the tools: it ships no bees,
cards, skills or workflow rules, and everything a hive does with bees comes from the cards and settings
you write. Tests: DocTests 50/50, StoreTests 296/296, WindowTests 230/230 on Arch under gtk4-broadwayd.

### What's in it

- **The hive:** Markdown entries with an SQLite FTS5 index, an MCP server (hive_brief, hive_recall,
  hive_read, hive_deposit, bee_*, team_review_*), and hooks that brief a session or a bee at start.
- **Bees:** Claude Code subagents made from the cards you write. Cards change through molts you approve,
  and background tasks have three access levels.
- **Roles and settings on a card:**
  - `role: reviewer`, `overseer`, `evolver`, `keeper`;
  - `evolve_after`;
  - `deposit_before_stop`.
- **Edit tasks** may run only the commands listed in `settings.md` (`edit_commands`; default `make` and
  `./build.sh`), plus skills.
- **Honey and misses:**
  - from you, from the main session through `bee_reward` (evidence required, never for a bee's own
    work), or from the hive itself;
  - each one records who gave it and why, and can be taken back.
  - An evolver bee reviews another bee by itself after enough new outcomes. Its small rule changes, which
    are appended rules each citing two or more outcomes, apply at once and can be undone.
- **Team reviews** with the bees you name: blind reviewers, and an overseer whose `## Reviewers` table
  awards them.
- **A PreToolUse hook** keeps reviewer and overseer bees to read-only git, `ls`, `wc` and `du`, in every
  permission mode. A missing hook binary fails open, so their agent files get Bash only while the hook
  is installed.
- **The window** (GTK 4 / libadwaita, in Fira Code):
  - the honeycomb scene, plus pages for bees, reviews, memory and briefs;
  - a conversation pane with a tab for each bee it starts, a model menu (`--model` per turn, kept per
    conversation) and local voice dictation (pw-record and whisper-cli, shown only when both are
    installed, with the text inserted for editing and never sent);
  - no CPU use at rest.
- **`hive install`** asks before turning off Claude Code's own auto memory (`--auto-memory off|keep` for
  scripts), and `hive uninstall` turns it back on only if install turned it off.
- One NativeAOT binary, no NuGet packages.

### Known

- Under about 926 px wide the window's header overflows.
- An exception inside the PreToolUse hook, or its 10 s timeout, lets the command through.
- Two awards can still race on `.tasks/.evolve-lock` for up to 5 s; after that the automatic review is
  skipped and the honey stands.
