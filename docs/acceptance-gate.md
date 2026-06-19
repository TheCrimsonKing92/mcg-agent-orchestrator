# Acceptance Gate

The acceptance gate runs after a goal's SDLC roles complete and the goal reaches Verified.
Before the gate runs, the goal branch is rebased onto current main.
That makes the gate verify the integrated result, not just the isolated branch.
The gate builds the integrated worktree.
It also runs the configured test suite against that worktree.
A clean, low-risk result is fast-forward-merged to main automatically.
Risky changes or goals that fail repeatedly are escalated to the operator for review.
