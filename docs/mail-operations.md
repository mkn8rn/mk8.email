# Mail operations

## Administrator access

Open `https://192.168.89.251:8443` from `192.168.89.0/24`. The site does not listen on a public interface.

The dashboard uses a private certification authority. Its certificate covers `admin.mk8n.com` and `192.168.89.251`.

The private authority key stays at `D:\keys\shitbox1\admin-ca\root-ca.key.pem`. Never copy that file to the server.

Trust only the public authority certificate on an administrator workstation. This command changes the current Windows user trust store.

```powershell
certutil -user -addstore Root D:\keys\shitbox1\admin-ca\root-ca.cert.pem
```

The server certificate expires on October 6, 2027. Issue and deploy a replacement before the final 14 days.

Deploy a replacement through the validation script. It checks expiry, names, the private key, and nginx before reload.

```sh
deploy-admin-certificate /root/admin-certificate.pem /root/admin-private-key.pem
```

Remove the two transfer files after a successful deployment. Keep the local authority files protected by their existing Windows access rules.

## Account work

Sign in as `admin@mk8n.com`. The dashboard accepts only an active `SuperAdmin` account.

Use the Accounts page to create an account. Use at least 16 characters for every account password.

Use the Accounts page to disable an account or change its password. A disabled account cannot authenticate or receive exact-address delivery.

Use the Domains page to create a mail domain. Use the same page to set its catch-all target.

The current `mk8n.com` catch-all target is `mk8n@mk8n.com`. Exact account addresses have priority over the catch-all route.

Every administrator action writes one JSON record to `/var/log/mk8email-admin/audit.jsonl`. The write completes before the response returns.

The audit record includes time, account identity, source address, action, target, and result. It never includes a password.

The audit log rotates each day and keeps 90 rotations. Nginx removes query strings from access logs to reduce accidental data capture.

The status page reads `/var/lib/mk8-mail-health/status.json`. The root health process writes this sanitized snapshot atomically.

The snapshot contains only health, queue, storage, backup, signature, and certificate metrics. The dashboard cannot control services or read mail.

## Service health

Run the strict deep check after each deployment, restart, update, or network change.

```sh
sudo mk8-mail-health --deep --strict
```

The check validates required services, databases, Valkey, ClamAV, signatures, queues, disk use, certificates, the dashboard, and configuration syntax.

The timer runs the normal check regularly. It writes a journal event only when health changes or the error set changes.

The check also verifies the backup, health, and AIDE timers. It reports a failed backup or failed integrity service.

The health check skips a run while a backup holds its exclusive lock. This behavior prevents false service failures during the snapshot interval.

```sh
systemctl status mk8-mail-health.timer
cat /var/lib/mk8-mail-health/status
journalctl -t mk8-mail-health --since today
```

The health directory must use owner `root`, group `mk8email`, and mode 2750. The snapshot must use the same owner and group with mode 0640.

Inspect all failed units after an unexpected restart.

```sh
systemctl --failed
systemctl status postfix dovecot rspamd clamav-daemon valkey-server postgresql mk8email-admin nginx
```

Do not start Matrix as part of a mail recovery. Matrix has a separate migration lock and data requirement.

## Integrity monitoring

AIDE checks filesystem integrity near 01:30 each day. A random delay spreads the start across 15 minutes.

The schedule finishes before the daily backup window. Its service uses low CPU and input-output weights during the scan.

The service uses an eight-gigabyte memory threshold and a ten-gigabyte hard limit. Its runtime cannot exceed 20 minutes.

The `s-nail` command sends changed-file reports through local Postfix to `admin@mk8n.com`. Reports stay quiet when no change occurs.

AIDE does not scan mutable mail, backup, Matrix media, health snapshot, or dashboard audit-log data. Service-specific rules handle other mutable system data.

Test the complete local alert path after a Postfix or AIDE change. The test removes its message after successful delivery.

```sh
/usr/local/lib/mk8email/tests/aide_alert_smoke
```

Review every report before accepting a new baseline. Verify packaged files and explain every custom-file change first.

```sh
debsums --silent
less /var/log/aide/aide.log
stat /var/lib/aide/aide.db /var/lib/aide/aide.db.new
```

Accept a reviewed baseline with explicit ownership and permissions. Never accept a baseline after unexplained changes.

```sh
install -o root -g root -m 0600 /var/lib/aide/aide.db.new /var/lib/aide/aide.db
```

The weekly debsums job separately checks installed package files. An empty `debsums --silent` result means that package checksums match.

## Queue work

Postfix owns the durable message queue. Do not delete queue files through the file system.

Inspect the queue with supported Postfix commands. Message content can contain private data, so restrict `postcat` output.

```sh
postqueue -p
postqueue -j
postcat -q QUEUE_ID
```

Use a forced queue run only after you correct a temporary delivery failure.

```sh
postqueue -f
```

The health check reports more than 100 queued messages. It also reports any message older than one day.

Do not purge a deferred message until you identify its sender, destination, failure, and retention requirement.

## Mail logs

Use bounded journal queries. Avoid continuous debug logging on the production host.

```sh
journalctl -u postfix --since '-1 hour' --no-pager
journalctl -u dovecot --since '-1 hour' --no-pager
journalctl -u rspamd --since '-1 hour' --no-pager
journalctl -u mk8email-admin --since '-1 hour' --no-pager
```

ClamAV writes only operational and detection events to `/var/log/clamav/clamav.log`. Clean scan events stay disabled.

Rspamd clean-message logging stays disabled. Postfix records queue identifiers, delivery results, and protocol failures through the journal.

Nginx uses `/var/log/nginx/admin-access.log` and `/var/log/nginx/admin-error.log` for the LAN dashboard.

The journal has a one-gigabyte limit and a 30-day limit. Log rotation bounds the separate mail and dashboard files.

## Storage checks

The health check reports mail storage at 90 percent use. Investigate growth before that limit.

```sh
df -h /var/vmail /var/lib/postgresql /var/backups
du -x -h --max-depth=2 /var/vmail | sort -h | tail -n 30
du -x -h --max-depth=2 /var/backups/mk8 | sort -h | tail -n 30
```

Do not run or open files below `/var/vmail`. Treat all message content as untrusted data.

## Backups

`mk8-backup.timer` creates one local snapshot each day. It retains local and encrypted exports for 14 days.

The backup briefly stops submission, delivery, the administrator application, and Valkey. Rspamd and ClamAV stay active.

The measured small-system interruption is approximately five seconds. The script restarts each service that was active before the snapshot.

Each snapshot contains both PostgreSQL databases, PostgreSQL roles, Maildir data, the Postfix spool, Valkey data, configuration, and Matrix media.

Each snapshot also records installed packages, manual packages, the Debian release, the kernel, APT sources, and repository keys.

The configuration archive includes the reviewed AIDE baseline. This file supports integrity checks after a complete host restore.

The configuration archive excludes `/etc/mk8email/bootstrap-secrets`. Remove temporary password files immediately after each account command.

Mail and Matrix media use hard-linked incremental copies. Database dumps and configuration archives are complete for each snapshot.

The server encrypts each export with `age`. It stores only the public recipient at `/etc/mk8email/secrets/backup-age-recipient`.

The private identity stays at `D:\keys\shitbox1\backup\mk8-backup-age-identity.txt`. Do not copy it to the server.

Run an extra backup before a deployment, schema change, certificate change, or public network change.

```sh
systemctl start mk8-backup.service
systemctl status mk8-backup.service
```

Pull the newest encrypted export to the workstation after the backup finishes.

```powershell
pwsh -NoProfile -File deploy\scripts\pull-encrypted-backup.ps1
```

The pull script selects one complete export and verifies its SHA-256 value before final placement. It never transfers the private identity.

Keep another encrypted copy on separate storage. A workstation copy alone does not protect against theft or simultaneous disk loss.

## Restore test

Test restoration each month and after a backup code change. Use a separate recovery path and temporary database names.

Decrypt the selected export only in `D:\temp\mk8.email\restore-test`. Remove the decrypted data after the test.

Verify the outer SHA-256 file before decryption. Verify the inner `SHA256SUMS` file before any restore command.

The repository test checks archive contents, PostgreSQL dumps, configuration, account rows, catch-all data, and expected ownership.

```sh
deploy/tests/backup_restore_smoke /var/backups/mk8/BACKUP_NAME
```

For a database test, create an empty temporary database. Restore with `pg_restore --exit-on-error` and run read-only validation queries.

Do not restore over the live database during a test. Do not extract a configuration archive over the live root file system.

## Disaster recovery

Keep the affected host offline after confirmed compromise. Preserve its disks and logs before repair.

Install the same Debian release on a clean host. Apply all security updates and the versioned hardening files.

Restore configuration only after checksum validation. Review private keys and rotate any value that an attacker could have read.

Restore PostgreSQL roles before both database dumps. Restore Maildir data, the Postfix spool, Valkey data, and Matrix media with numeric ownership.

Mount `/var/vmail` with `nosuid`, `nodev`, and `noexec` before service start. Confirm `vmail` uses user and group identifier 5000.

Install the recorded immutable release from `mk8email-release.txt`. Run all configuration tests while public forwarding remains closed.

Run the strict deep check, local protocol tests, malware tests, and queue recovery test. Open public traffic only after all tests pass.

Transfer test password files only into `/run`. Pass their paths to the smoke tests, then remove the files immediately.

Change all account passwords after a credential exposure. Replace the DKIM key and update DNS after a private-key exposure.

## Updates and releases

Review Debian security updates each week. Review unattended update results and pending reboots.

```sh
apt-get update
apt list --upgradable
systemctl status unattended-upgrades
test -e /var/run/reboot-required && cat /var/run/reboot-required
```

Do not add a compiler or Git checkout to the production host. Build signed source on the workstation or in CI.

Deploy only a reviewed commit through `deploy-production.ps1`. The script builds, tests, backs up, installs, validates, and rolls back on failure.

After a successful deployment, repeat the strict health check and live smoke tests. Pull the deployment backup off the host.

## Incident isolation

Use `deactivate-mail-stack` if mail processing itself can damage data. This command removes the ready marker and stops the mail-facing components.

Do not delete the Postfix queue during isolation. Keep nginx and SSH limited to the local network during investigation.

Block RouterOS forwarding when the host shows signs of system compromise. Preserve the public DNS values until you select a clean recovery target.

Use console access if SSH integrity is uncertain. Do not weaken password authentication or public firewall rules during recovery.

## Known residual risk

Mail parsers, antivirus engines, the Linux kernel, and native libraries can have unknown defects. Sandboxing reduces impact but cannot remove all risk.

ClamAV signatures cannot identify every new malicious file. Client devices must also isolate attachments and use current security software.

The mail and Matrix services share one kernel. A future kernel-level escape could cross service boundaries despite separate users and sandboxes.

Keep verified off-host backups and a tested rebuild process. These controls provide recovery when prevention fails.
