# AXIS OS rollout

Use it to bring devices to a newer AXIS OS. Never start with all devices.

1. `oadm_server_info`, then `oadm_devices_summary` with `group_by` `firmware`: which versions exist and how many devices
   have each.
2. Ask the user what they want: a version, "the latest for each model", or "at least version X". Ask which sites or
   tags. Do not choose a version yourself.
3. Narrow with the filter, for example `{"firmware": {"below": "12.11"}, "models": ["M3085-V"]}`. Check the count with
   `oadm_devices_summary`.
4. `oadm_tasks_catalog` with the filter. Look for the upgrade task. If it is not listed, it is not available over MCP:
   tell the user to run Upgrade AXIS OS in OADM. If it lists devices that cannot run it, the reasons are in the answer.
5. A firmware file that is not in OADM yet goes through `oadm_files_upload_link`. The file never passes you: give the
   user the command and wait.
6. `oadm_task_preview` with the task, the filter and the input. Show the user:
   - how many devices will run and the first ones,
   - the **skipped devices and each reason** ("No file for this model.", "Already up to date", "Needs AXIS OS 11.11 or
     later"),
   - the warnings. Devices restart during an upgrade. Cameras record nothing for a few minutes.
7. Start small: one device or one site first. After the user agrees, `oadm_task_run` with the `handle`.
8. Follow with `oadm_tasks_list` (the batch id). When it is done, check the devices with `oadm_devices_summary` by
   firmware and status. Devices that are `Unreachable` after the restart can need a few more minutes: check again before
   you call it a failure.
9. Only after the first group is fine, preview and run the rest. Never run a handle twice.

Do not downgrade, and do not skip a version the device needs first, unless the preview says it is fine.
