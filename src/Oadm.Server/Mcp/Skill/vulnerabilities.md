# Vulnerabilities

Use it for "which cameras are affected by Axis CVEs?". OADM matches its list of Axis vulnerabilities with each device's
AXIS OS. It only reads; the fix is an AXIS OS rollout.

<!-- TODO: the vulnerability tools come from the device tools branch (planned: a summary and a list). Name them here
once they exist. -->

1. `oadm_server_status` gives the date of the list. Say how old it is; an old list misses new CVEs.
2. The vulnerability summary (TODO, see above): how many devices are affected, by CVE and by severity. A match on an
   LTS track the record gives no fix for is "possible": report it apart and do not count it as affected.
3. For one CVE, list the affected devices with `oadm_devices_search` and the AXIS OS that fixes it.
4. Report: affected devices by severity (counts), the possible ones apart, and which AXIS OS fixes them.
5. Offer the fix: the [AXIS OS rollout](rollout-axis-os.md) on the affected devices, one small group first. Never start
   it without the user.

The data comes only from the CVE records. Do not add findings of your own or from other sources.
