# SD cards

Use it for "which SD cards are full or failed?" and to check, repair or format them.

1. `oadm_edge_storage_cards`: the counts per state first, then the cards. `state` `problem` or `warning` for the cards
   that need action; `used_above` for full cards.
2. It answers what the Edge storage page read last. `read_utc` null: nothing was read. Read the cameras with
   `oadm_edge_storage_read` (a filter, or `all_devices`), then ask again.
3. Report: counts per state, then the cards with a problem (camera, the reason), the worn ones (wear percent), the full
   ones, cameras without a card.
4. A change is the task `oadm.edge-storage.task` (`oadm_task_preview`, `action` `check`, `repair`, `retention` with
   `retentionDays`, `format`, `encryptionOn` or `encryptionOff`). Format and encryption on or off **delete every
   recording** on the cards; check, repair and format stop recording while they run. Say so to the user in plain words,
   name the cameras, and run only after a clear yes. Encryption on needs a passphrase: the user enters it through the
   secret command of the preview (see "Passwords").
