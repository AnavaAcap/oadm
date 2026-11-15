# Applications

Use it for "which applications run where?" and to change them.

1. `oadm_app_management_applications`: per application how many devices have it, run it, its versions and license
   problems, then one row per device and application. Filter with `application` or `status`.
2. It answers what the Applications page read last for the user. `read_utc` null or no devices: nothing was read. Read
   the devices with `oadm_app_management_read` (a filter, or `all_devices`), then ask again. An application's log:
   `oadm_app_management_log`; its settings on many devices: `oadm_app_management_settings`.
3. Report: each application with its device count, how many run it, the versions, devices with a license problem.
4. Start, stop, install, remove or change settings is the task `oadm.acap`: `oadm_tasks_catalog`, then
   `oadm_task_preview` (`action` `settings` with `application` and `values` by setting name). Only after the user agrees.
   A password setting goes in `passwords` by name (never in `values`); a license key is installed with `installLicense`
   on one device. The user enters both through the secret command of the preview (see "Passwords").
