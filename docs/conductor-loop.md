# Conductor Loop

The conductor loop evaluates eligible goals one tick at a time.
Each tick resolves the current lifecycle state for every goal it can advance.
It dispatches ready SDLC role tasks and reconciles completed dispatches.
Goals move through the SDLC roles until they reach the Verified state.
For a clean, low-risk goal, the conductor rebases the goal branch onto main.
It then runs the acceptance gate, including build and test verification.
If acceptance passes, it fast-forward-merges the goal branch.
Risky goals or goals that fail repeatedly are escalated to the operator.
