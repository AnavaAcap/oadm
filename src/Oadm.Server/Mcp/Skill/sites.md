# Sites

Use it for "is a site down?" and "which devices are behind a site node?". It only reads.

1. `oadm_sites_list`: each site with its status, device count, the site node's version and when it was last connected.
2. Devices of one site: `oadm_devices_summary` with `sites` in the filter and `group_by` `status`. `SiteOffline` means the
   site node is not connected, not that the devices are down.
3. A site that is `Offline`: the site node lost its connection to OADM. Check its `last_seen`, and `oadm_logs_search`
   with the site's name as `text`.
4. `update_available`: a newer site node exists. Site nodes are updated by hand at the site.
5. Report: sites by status, the devices behind each offline site (counts), what to check at the site.

Binding a site node and its pairing code are done in OADM only.
