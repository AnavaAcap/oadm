# Event rules

Use it for "what do my rules do?" and "why did this rule not act?". It only reads.

1. `oadm_rules_list`: each rule with what it watches, what it does, its state and when it last fired. `problem` says why
   a rule cannot run.
2. `oadm_rules_get` for one rule in full: conditions, actions, limits. Passwords and logins are never shown.
3. What fired: `oadm_events_log` with `rule_id`, newest first. `kind` `action` with `result` `failed` shows the actions
   that failed and why.
4. A rule that did not act: check in this order. Is it on? Did its event come (`oadm_events_log` with `kind` `event` and
   `text`)? Did a condition or a limit stop it (`kind` `rule`, `result` `skipped`)? Did the action fail (`kind` `action`)?
5. Recipients: `oadm_rules_catalog` lists them by name and kind.
6. Report: the rule, what happened in the log, the cause, the fix. A change is `oadm_rules_save_preview` then the run,
   only after the user agrees.
