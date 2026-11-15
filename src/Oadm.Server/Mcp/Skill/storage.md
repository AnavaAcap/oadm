# SD cards

Use it for "which SD cards are full or failed?" and to check, repair or format them.

1. `oadm_edge_storage_cards`: the counts per state first, then the cards. `state` `problem` or `warning` for the cards
   that need action; `used_above` for full cards.
2. It answers what the Edge storage page read last. `read_utc` null: nothing was read. Tell the user to open Edge storage
   in OADM and choose the cameras. Do not read the cameras yourself.
3. Report: counts per state, then the cards with a problem (camera, the reason), the worn ones (wear percent), the full
   ones, cameras without a card.
4. A change is the task `oadm.edge-storage.task` (`oadm_task_preview`, `action` `check`, `repair`, `retention` with
   `retentionDays`, `format` or `encryptionOff`). Format and encryption off **delete every recording** on the cards;
   check, repair and format stop recording while they run. Say so to the user in plain words, name the cameras, and run
   only after a clear yes. Turning encryption on needs a passphrase: it stays on the Edge storage page.
