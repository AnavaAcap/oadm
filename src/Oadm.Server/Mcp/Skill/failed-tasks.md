# Failed tasks

Use it for "what failed?" and after a rollout. It only reads, until the user agrees to a retry.

1. `oadm_tasks_summary` with `status` `["Failed"]` and `group_by` `message`: the failures grouped by their error, the
   largest group first. Add `since`, `plugin` or `batch` to narrow.
2. For each large group, `oadm_tasks_get` with its `example_task_id`: the steps say where it stopped, the log says why.
3. The devices of a group: `oadm_tasks_list` with the same filters and `text` set to a part of the error.
4. Common causes: the device is not `Ok` (check `oadm_devices_summary` by status), a wrong password (a new login with
   `oadm_devices_credentials_preview` or `oadm_devices_login_preview`), a missing file, AXIS OS too old for the task.
5. Report per group: the error, how many devices, the cause and what to do.
6. A retry is a new run: `oadm_task_preview` with the failed devices' ids, after the cause is fixed. Never retry a
   failure that will fail again (a wrong password, an unsupported device).
