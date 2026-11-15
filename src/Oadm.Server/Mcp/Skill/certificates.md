# Certificates

Use it to see the certificates OADM holds and to create new ones. Private keys never pass you.

1. `oadm_pki_certificates` lists the certificates: name, issuer, valid until, status. Start with a filter on what
   ends soon or is already ended, not the whole list.
2. To create a certificate, ask the user for the name, the names and addresses it must hold, how long it is valid and
   which CA signs it. Do not guess addresses: an agent that connects to a name the certificate does not hold refuses the
   server.
3. `oadm_pki_certificate_create_preview` checks the input and answers the plan and a `handle`. Show it to the user.
   After the user agrees, `oadm_pki_certificate_create_run` with the `handle`.
4. Putting certificates on devices is a task. `oadm_tasks_catalog` with the filter shows what is offered. If the task
   is not listed, it is not available over MCP: tell the user to run it in OADM (the Devices page, Security).
5. For a task use `oadm_task_preview`. Say clearly that devices with IEEE 802.1X on can lose their network access when
   the new certificate is not trusted by the RADIUS server. Start with one device.
6. After the run, check with `oadm_pki_certificates` and `oadm_devices_summary` by status. `CertificateChanged` means a
   device now shows another certificate than OADM trusts. Tell the user. Do not accept it for them.

Importing a CA with its key, removing a CA and PKI settings are done in OADM only.
