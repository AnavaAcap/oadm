# Configure devices

Use it for "set capture frequency 50 Hz on all lobby cameras" and other settings of many devices.

1. Read first: `oadm_config_parameters` with a word of the setting or its exact name. It groups the devices by value and
   says which could not be read. Take the exact name and a value the devices already use; do not guess either.
2. Narrow the devices with a filter (`oadm_devices_search`): only those that need the change.
3. `oadm_tasks_catalog` with the filter, `task` `oadm.device-config.configure`: how many can run it and its input.
4. `oadm_task_preview` with `input` `{"settings": [{"name": "ImageSource.I0.Sensor.CaptureFrequency", "value": "50Hz"}]}`.
5. Show the user the plan: the task name, how many devices, the **skipped devices with their reasons** and every warning.
   A warning that devices may become unreachable (network, HTTPS, ports, login) needs the user's clear yes; start with one
   device.
6. After the user agrees, `oadm_task_run` with the `handle`. Follow it with `oadm_tasks_list` and the batch. A device
   that does not have the setting or refuses the value skips it: `oadm_tasks_get` shows why.
7. Read again with `oadm_config_parameters` to show the result.

Action rules and stream profiles of the same task: `actionRules`, `streamProfiles` in the input (read them with
`oadm_config_action_rules`, `oadm_config_stream_profiles`). A password setting is sent with an empty value: the
preview answers `secret_links`, show the user the command (see "Passwords"), never ask for the value.
Import configuration takes one `.json` from `oadm_files_upload_link`; Export configuration and Create configuration file
put the file in the task's log.
