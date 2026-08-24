# Negative controls: source-size admission on every acceptance path

Before production changes, the focused tests recorded these RED controls:

- Inline landing reached its lease callback and failed with `The source-size rejection must run before stable-slot acquisition.`
- Manual CLI acceptance reached its slot selector and failed with `The source-size rejection must run before the CLI stable-slot selector.`
- Two-member cohort acceptance acquired its cohort lease and failed with `The cohort stable-slot lease must not be acquired for a ratchet breach.`
- Merge-train acceptance acquired its cohort lease and failed with `The merge-train stable-slot lease must not be acquired for a ratchet breach.`
- A no-TRX ratchet result classified as `InfrastructureFailure` instead of the expected `Failed` content outcome.

The final GREEN controls are the `*RatchetBreach*` and `*ViolatingAuthority*` method filters, plus the compliant
inline, CLI, cohort, and merge-train cases named in the Developer verification receipt. Removing any new
pre-slot call must reacquire the corresponding lease or invoke its verifier; removing the ratchet classifier
branch must restore the `InfrastructureFailure` mismatch.
