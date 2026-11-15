# Applications

Use it for "which applications run where?". It only reads.

1. `oadm_app_management_applications`: per application how many devices have it, run it, its versions and license
   problems, then one row per device and application. Filter with `application` or `status`.
2. It answers what the Applications page read last for the user. `read_utc` null or no devices: nothing was read. Tell
   the user to open Applications in OADM and choose the devices. Do not read the devices yourself.
3. Report: each application with its device count, how many run it, the versions, devices with a license problem.
4. Start, stop, install or remove is a task of the Applications plugin: `oadm_tasks_catalog`, then `oadm_task_preview`.
   Only after the user agrees.
