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
   View certificates (`oadm.pki.view`) lists each device's certificates in the task's log; Delete certificates
   (`oadm.pki.delete`) takes those names per device. A deleted certificate cannot be restored: say so before the run.
   EST set up and remove on cameras is `oadm.est.setup`.
5. For a task use `oadm_task_preview`. Say clearly that devices with IEEE 802.1X on can lose their network access when
   the new certificate is not trusted by the RADIUS server. Start with one device.
6. After the run, check with `oadm_pki_certificates` and `oadm_devices_summary` by status. `CertificateChanged` means a
   device now shows another certificate than OADM trusts. Tell the user. Do not accept it for them.

CAs (create, import, delete, back up a key) and PKI policies are tools of PKI too, each a preview and a run; a CA key
backup's password goes through the secret command (see "Passwords").
