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
   Password settings and license keys are set in OADM.

## Install from a vendor's ZIP

1. Upload the ZIP as it is with `oadm_files_upload_link` (a ZIP the user attached in the Assistant already has its file
   id). Never unpack it yourself.
2. `oadm_task_preview` with task `oadm.acap`, `action` `install` and `zipFileId`. OADM reads the `.eap` builds in it and,
   when the vendor added one, its `oadm-packages.json`, and answers per group of devices which file it gets in `details`;
   devices without a fitting build are in `skipped`.
3. When two builds fit a group equally the preview answers an error at `mapping` naming the group and the files. Ask the
   user which file, then preview again with `mapping`: `[{"axisOs": "12.11.77", "architecture": "aarch64", "model":
   "P3265-V", "file": "lpr_2.6_aarch64.eap"}]`. Never choose for the user.
4. Show the user the plan per group, the files with their SHA-256 and the warnings. The user runs it; then
   `oadm_task_run` with the handle.

Loose `.eap` files work the same with `files` (each an upload's `fileId`); `mapping` then names the file ids.
