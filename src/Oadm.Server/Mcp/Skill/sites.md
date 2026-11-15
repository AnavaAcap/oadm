# Sites

Use it for "is a site down?", "which devices are behind a site node?" and to bind, move or remove a site.

1. `oadm_sites_list`: each site with its status, device count, the site node's version and when it was last connected.
2. Devices of one site: `oadm_devices_summary` with `sites` in the filter and `group_by` `status`. `SiteOffline` means the
   site node is not connected, not that the devices are down.
3. A site that is `Offline`: the site node lost its connection to OADM. Check its `last_seen`, and `oadm_logs_search`
   with the site's name as `text`.
4. `update_available`: a newer site node exists. Site nodes are updated by hand at the site.
5. Report: sites by status, the devices behind each offline site (counts), what to check at the site.

## Change sites (administrators)

1. A new site node: `oadm_sites_waiting` lists the waiting nodes with their fingerprint. Call `oadm_sites_bind_preview`
   with its `node_id`, `fingerprint` and a name. The user compares the fingerprint with the OADM Site Node app and enters
   the pairing code through `secret_links`, never in the chat. Then `oadm_sites_bind_run`.
2. A new node for an old site: bind it, then `oadm_sites_replace_preview` moves the devices; a device it cannot reach stays.
3. Remove: `oadm_sites_remove_preview` keeps the devices as Site offline unless `keep_devices` is false. Name the device
   groups that use the site.
4. Accept site nodes off (`oadm_sites_accept_preview`) cuts every site off: say so first.

