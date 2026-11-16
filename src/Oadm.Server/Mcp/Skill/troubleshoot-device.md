# Troubleshoot one device

Use it for "what is wrong with this camera?". It only reads, and it reads what OADM knows before it asks the device.

1. Find the device: `oadm_devices_search` with `search` (address, host name or UPnP name). Ask the user when more than
   one matches. Use its `id` from then on.
2. `oadm_devices_get` gives everything OADM knows: status and its detail, AXIS OS, clock offset, certificate and its
   trust, HTTPS, IEEE 802.1X, site, tags, last seen.
3. Its history: `oadm_tasks_summary` with `device_id` and `group_by` `message`, then `oadm_tasks_get` for the newest
   failed task. `oadm_events_log` with `text` set to its address shows rules that fired on it.
4. The server's view: `oadm_logs_search` with its address as `text` and a `since`.
5. Plugin pages, when their tools are listed: `oadm_compliance_summary` with the device's id in the filter,
   `oadm_hardening_results`, `oadm_edge_storage_cards`, `oadm_radius_log` with its address, `oadm_dhcp_leases`.
6. Only when all this does not explain it, a picture with `oadm_devices_snapshot`, or a read command through
   `oadm_vapix_command_preview`. Say why you ask the device.
7. Report: the problem in one line, the facts that show it, the next step with the real name of the action in OADM.

Status `Unreachable`: check its site (`oadm_sites_list`) before the device. `CredentialsRequired`: a login with
`oadm_devices_credentials_preview` or `oadm_devices_login_preview`. `CertificateChanged`: tell the user, never accept it for them.
