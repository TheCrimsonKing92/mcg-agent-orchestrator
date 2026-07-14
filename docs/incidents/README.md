# Operational Incidents

This directory is the durable in-repo ledger for operational incidents: what broke, why the operator believed that diagnosis, what evidence proved or falsified it, what landed, and what still remains.

Create one file per incident, named `YYYY-MM-DD-<slug>.md`. Write entries when the incident is diagnosed, not after the story is retrospectively perfect. Prefer short, receipt-backed notes over polished narratives that hide uncertainty.

Conventions:

- Link related backlog ids and goal ids.
- Cite repo-derivable facts only: backlog bodies, conduct events, logs, git history, committed files, and checked-in triage notes.
- When the backlog item is the source, say so explicitly.
- Keep residuals separate from fixes; an active or open goal is not a landed fix.
- Add supporting material beside the incident when a focused artifact should survive with it.

