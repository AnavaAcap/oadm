# Applications

Use it for "which applications run where?" and to change them.

1. `oadm_app_management_applications`: per application how many devices have it, run it, its versions and license
   problems, then one row per device and application. Filter with `application` or `status`.
2. It answers what the Applications page read last for the user. `read_utc` null or no devices: nothing was read. Tell
   the user to open Applications in OADM and choose the devices. Do not read the devices yourself.
3. Report: each application with its device count, how many run it, the versions, devices with a license problem.
4. Start, stop, install, remove or change settings is the task `oadm.acap`: `oadm_tasks_catalog`, then
   `oadm_task_preview` (`action` `settings` with `application` and `values` by setting name). Only after the user agrees.
   Password settings and license keys are set in OADM.
