# SD cards

Use it for "which SD cards are full or failed?". It only reads.

1. `oadm_edge_storage_cards`: the counts per state first, then the cards. `state` `problem` or `warning` for the cards
   that need action; `used_above` for full cards.
2. It answers what the Edge storage page read last. `read_utc` null: nothing was read. Read the cameras with
   `oadm_edge_storage_read` (a filter, or `all_devices`), then ask again.
3. Report: counts per state, then the cards with a problem (camera, the reason), the worn ones (wear percent), the full
   ones, cameras without a card.
4. Format, repair and encryption delete recordings: they stay on the Edge storage page. Point the user there.
