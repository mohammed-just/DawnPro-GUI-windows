# DeepSeek Harness Migration — DAWN PRO / DAWN PRO2 .NET 10 WPF

This document records how to continue this project with a DeepSeek-backed coding-agent harness, independent of the previous Codex session. It is informational guidance for the next harness/operator, not executable configuration. It contains **no real API keys or credentials** — every credential below is a placeholder.

**Primary authority:** official DeepSeek API documentation (`https://api-docs.deepseek.com/`), specifically the "Integrate with AI Tools" guide (`/guides/coding_agents`) and the per-tool integration pages under `/quick_start/agent_integrations/`. The facts below were cross-checked against those official pages and the official `deepseek-ai/awesome-deepseek-agent` repository as of **2026-08-17**. Because this handoff was written offline, **re-verify the live official pages before first use** — model identifiers and commands change.

---

## 1. Recommendation

- **Primary harness: Claude Code + DeepSeek** (Anthropic-compatible endpoint).
- **Fallback harness: Deep Code** (DeepSeek-V4-native terminal assistant).

### Why Claude Code is primary for this specific project

- This repository is a **large, Windows-native, multi-language monorepo** (Python oracle + a multi-project `.NET 10` solution plus physical-harness test projects). It needs full tool execution (shell/build/test), file editing, sub-agents, and long autonomous runs.
- Claude Code has the most mature tool-execution, sub-agent, session resume, project-instruction (`CLAUDE.md`), and context-compaction surface among the officially listed DeepSeek integrations.
- DeepSeek publishes a first-class Claude Code integration with explicit environment-variable mapping, including the `[1m]` 1M-context Pro model for the main loop and the cheaper Flash model for sub-agents — directly matching this project's large-repository and long-running needs.
- `CLAUDE_CODE_AUTO_COMPACT_WINDOW` gives deterministic long-session context management, which matters for the multi-phase offline→review→approve→PREPARE workflow.
- Native Windows support (install via npm, configure via PowerShell) matches the authoritative working tree at `C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16`.

### Why Deep Code is the fallback

- Deep Code is an open-source terminal assistant that DeepSeek documents as adapted specifically for the **DeepSeek-V4 series**, with deep thinking, **reasoning-effort control**, and **Agent Skills** (the closest analogue to this project's "skills" workflow).
- It is a good fallback if the Claude Code/Anthropic-compatible route is unavailable or the operator prefers a DeepSeek-native tool.
- It is newer and has a smaller ecosystem than Claude Code, so it is the fallback rather than the primary.

---

## 2. Current model identifiers / settings

Current (2026) model identifiers:

- `deepseek-v4-pro` — primary/large model; Pro tier.
- `deepseek-v4-flash` — fast/cheaper model; good for sub-agents and smaller tasks.
- `deepseek-v4-pro[1m]` — the 1M-context variant of Pro (used in the official Claude Code mapping as the main model).

Retired/deprecated aliases (do **not** use):

- `deepseek-chat` (formerly → V4-Flash non-thinking)
- `deepseek-reasoner` (formerly → V4-Flash thinking)

These legacy aliases were retired on **2026-07-24**. Use the explicit `deepseek-v4-*` names.

Context and reasoning:

- V4 models advertise a 1M-token context window (Pro and Flash), a large step up from the V3-era 128K.
- V4 has a native "thinking" mode with reasoning-effort control. In Claude Code, effort is set via `CLAUDE_CODE_EFFORT_LEVEL` (e.g. `max`).

---

## 3. Provider / API configuration (placeholders only — no real keys)

Base URLs:

- OpenAI-compatible: `https://api.deepseek.com` (also `https://api.deepseek.com/v1`)
- Anthropic-compatible: `https://api.deepseek.com/anthropic`

API key: obtained from the DeepSeek Platform (`platform.deepseek.com` → API Keys). In every snippet below, replace the placeholder `<YOUR_DEEPSEEK_API_KEY>` with the real key. **Never commit the real key.**

---

## 4. Primary harness — Claude Code + DeepSeek

### 4.1 Install

Claude Code is installed as a terminal program. The standard install is:

```powershell
npm install -g @anthropic-ai/claude-code
```

On Windows this requires Node.js. (Re-verify the current install command against the official Claude Code/DeepSeek pages before use.)

### 4.2 Configure (Windows / PowerShell)

Official DeepSeek Claude Code environment mapping (PowerShell form). Set these for the current shell or persist them via the user environment / profile:

```powershell
$env:ANTHROPIC_BASE_URL="https://api.deepseek.com/anthropic"
$env:ANTHROPIC_AUTH_TOKEN="<YOUR_DEEPSEEK_API_KEY>"
$env:ANTHROPIC_MODEL="deepseek-v4-pro[1m]"
$env:ANTHROPIC_DEFAULT_OPUS_MODEL="deepseek-v4-pro[1m]"
$env:ANTHROPIC_DEFAULT_SONNET_MODEL="deepseek-v4-pro[1m]"
$env:ANTHROPIC_DEFAULT_HAIKU_MODEL="deepseek-v4-flash"
$env:CLAUDE_CODE_SUBAGENT_MODEL="deepseek-v4-flash"
$env:CLAUDE_CODE_EFFORT_LEVEL="max"
$env:CLAUDE_CODE_AUTO_COMPACT_WINDOW="786432"
```

Equivalent Bash/Linux or WSL form:

```bash
export ANTHROPIC_BASE_URL="https://api.deepseek.com/anthropic"
export ANTHROPIC_AUTH_TOKEN="<YOUR_DEEPSEEK_API_KEY>"
export ANTHROPIC_MODEL="deepseek-v4-pro[1m]"
export ANTHROPIC_DEFAULT_OPUS_MODEL="deepseek-v4-pro[1m]"
export ANTHROPIC_DEFAULT_SONNET_MODEL="deepseek-v4-pro[1m]"
export ANTHROPIC_DEFAULT_HAIKU_MODEL="deepseek-v4-flash"
export CLAUDE_CODE_SUBAGENT_MODEL="deepseek-v4-flash"
export CLAUDE_CODE_EFFORT_LEVEL="max"
export CLAUDE_CODE_AUTO_COMPACT_WINDOW="786432"
```

### 4.3 Windows native vs WSL

- **Windows native (preferred):** the authoritative working tree is `C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16`, and the physical harness (HID/USB, DAC) is Windows-only. Run Claude Code natively in PowerShell from that directory so paths and the pinned `.NET` SDK (`C:\Users\mohammed\.dotnet\dotnet.exe`) resolve correctly.
- **WSL:** usable for offline source/tests, but the physical phases require the Windows host and the Windows `.NET` SDK. If using WSL, keep the Windows tree as the source of truth and do not redirect work to any other copy.

### 4.4 Launch in this repository

```powershell
cd "C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16"
claude
```

Then provide the first prompt (see section 8).

### 4.5 Resume / reopen

Claude Code persists session history; use its resume/continue command (e.g. `claude --continue` / `claude --resume`, or the in-session resume picker). Re-verify the exact current flag against the live Claude Code docs.

### 4.6 Tool execution / large-repo / long-run notes

- Claude Code executes shell commands and edits files, so it can run the `.NET` builds/tests and the Python oracle directly on the Windows host.
- Use `deepseek-v4-pro[1m]` for the main loop (large context for multi-phase work) and `deepseek-v4-flash` for sub-agents (cheaper).
- `CLAUDE_CODE_AUTO_COMPACT_WINDOW=786432` enables automatic compaction for long autonomous runs.
- `CLAUDE_CODE_EFFORT_LEVEL=max` selects maximum reasoning effort for safety-critical review/verification steps.

---

## 5. Fallback harness — Deep Code

Deep Code is documented as an open-source terminal coding assistant adapted for the DeepSeek-V4 series, supporting deep thinking, reasoning-effort control, Agent Skills, and MCP.

### 5.1 Configure (placeholder key only)

Create `~/.deepcode/settings.json` (Linux/WSL) with the DeepSeek API key and model settings. On Windows, use the equivalent user-profile location the tool documents. Replace the key placeholder:

```json
{
  "api_key": "<YOUR_DEEPSEEK_API_KEY>",
  "model": "deepseek-v4-pro"
}
```

(Re-verify the exact `settings.json` schema against the live official Deep Code integration page before use.)

### 5.2 Launch

```powershell
cd "C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16"
deepcode
```

Use `deepseek-v4-flash` for lighter/cheaper work and reasoning-effort control for safety-critical steps.

---

## 6. Other officially listed integrations (brief)

- **OpenCode** — run `opencode`, `/connect`, choose `deepseek`, enter the API key, select `DeepSeek-V4-Pro` (or Flash).
- **OpenClaw** — `openclaw plugins install @openclaw/deepseek-provider`, `openclaw gateway restart`, then onboarding (`deepseek/deepseek-v4-pro` or `deepseek/deepseek-v4-flash`).
- **Pi / pi-coding-agent** — `npm install -g @earendil-works/pi-coding-agent`; add DeepSeek as an OpenAI-compatible provider via `models.json`; select `deepseek` + DeepSeek-V4-Pro/Flash.

These are alternatives; the two recommendations above are preferred for this project's Windows + physical-harness + long-run profile.

---

## 7. What the new harness must read first

In this order:

1. `PROJECT-HANDOFF-NEW-HARNESS.md` — authoritative current state, fingerprints, candidates, safety invariants, and continuation sequence.
2. `DEEPSEEK-HARNESS-MIGRATION.md` — this file (harness setup).
3. `PROJECT-CONTINUATION.md` — historical engineering/continuation context (read after the handoff; older paths inside it are superseded by the handoff).

Then, before any edit, verify the working directory and re-read the safety invariants.

---

## 8. Ready-to-paste first prompt

Paste this into the new harness after launching it in the working tree:

```text
You are continuing the DAWN PRO / DAWN PRO2 .NET 10 WPF project.

Read these files first, in full, before doing any work:
1. PROJECT-HANDOFF-NEW-HARNESS.md
2. DEEPSEEK-HARNESS-MIGRATION.md
3. PROJECT-CONTINUATION.md

The authoritative working tree is:
C:\Users\mohammed\Documents\moondrop gui - copy 2026-08-16

The protected original (do NOT modify or use as the working tree) is:
C:\Users\mohammed\Documents\moondrop gui

Preserve the technical safety invariants recorded in PROJECT-HANDOFF-NEW-HARNESS.md
(fail-closed physical operations, default-safe test exclusion even with leaked
environment variables, strict process lineage, authenticated direct parent/topology,
no generic shell/dotnet/testhost allowlisting, coherent process identity, complete
runtime-manifest binding, manifest-derived hashes, reparse/junction protection,
canonical artifact publication, diagnostic secret/token redaction, control-character
safety, offline topology tests that cannot reach hardware, exact device identity checks,
read-only PREPARE that does not write, and EXECUTE requiring a separate explicit decision).

You do NOT have to reproduce the previous Codex subagent workflow or its broken
subagent transport. Use whatever reliable independent review/approval mechanism this
harness provides, as long as the safety invariants are preserved or improved.

Do not run PREPARE, EXECUTE, or RECOVERY until every intended gate is satisfied and,
for EXECUTE/RECOVERY, the user separately authorizes it. Start by inspecting the current
repository state and reporting a concise plan.
```

---

## 9. Reliability caveats

- Model identifiers, retirement dates, and integration commands change frequently. Re-verify `deepseek-v4-pro`, `deepseek-v4-flash`, the `[1m]` suffix, and each install/connect command against the live `api-docs.deepseek.com` pages before first use.
- This document intentionally omits the actual API key; the operator must supply their own key and keep it out of version control (it is covered by the `.gitignore` `.env`/secret rules).
- The physical harness is Windows-only; do not move the authoritative work to WSL or another path.
