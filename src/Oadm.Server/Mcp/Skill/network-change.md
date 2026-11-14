# Network change

Use it for IP addresses, DHCP or static settings, DNS, NTP and similar. A wrong change takes devices off the network
and they cannot be reached again from OADM. Treat every network change as risky.

1. `oadm_server_info` and `oadm_devices_summary` by site and status. Do not change devices that are already
   `Unreachable`.
2. Ask the user for the exact outcome (the new addresses or range, the gateway, the NTP server) and for the scope. If a
   value is missing, ask. Do not guess addresses.
3. For the time, `oadm_ntp_status` shows the OADM NTP server. The task "Use OADM NTP server" points devices at it.
   `oadm_dhcp_status` and `oadm_dhcp_leases` show what the DHCP server gave out.
4. `oadm_tasks_catalog` with the filter. If the task is not listed, it is not available over MCP: tell the user to
   run it in OADM.
5. `oadm_task_preview`. Show the plan, the skipped devices with their reasons and **every warning**. Say plainly when
   a warning means devices can become unreachable.
6. Run first on one device the user can reach, or one device per site. Then `oadm_task_run` with the `handle`.
7. After the run: `oadm_tasks_list` for the batch, then `oadm_devices_summary` by status. A device that does not come
   back is reported to the user with its id. Do not repeat the change to fix it.
8. Widen the scope only when the first devices are fine and the user agrees.

Devices at a site are reached through their site node. If a site goes `SiteOffline`, stop and tell the user.
