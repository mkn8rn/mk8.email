# Production deployment

## Production design

The production mail path uses Postfix, Dovecot, Rspamd, ClamAV, Valkey, PostgreSQL, nginx, and the .NET 10 administrator application.

Postscreen rejects invalid SMTP clients before they reach Postfix. Postfix then applies address, relay, rate, TLS, and sender-ownership rules.

Rspamd checks accepted message content through the Postfix Milter interface. ClamAV scans MIME parts, archives, documents, programs, macros, and suspicious content.

Postfix delivers local mail through Dovecot LMTP. Dovecot stores Maildir data below `/var/vmail` and provides encrypted IMAPS access.

Authenticated submission uses TCP port 587 with STARTTLS or port 465 with implicit TLS. Both paths reject a sender that the account does not own.

PostgreSQL stores domains, accounts, password hashes, quotas, and alias routes. The Razor Pages application is the supported account management path.

The dashboard reads a root-generated health snapshot. It never receives service control, queue access, or message access.

The custom .NET SMTP and IMAP services are experimental. Production systemd units do not start those services.

## Durable message handling

Postfix keeps each accepted raw message in its queue. An SMTP success reply means that Postfix has accepted queue responsibility.

Postfix keeps a message when local delivery or remote delivery has a temporary failure. It retries with bounded backoff for five days.

Postfix creates a delivery status notification when permanent delivery fails. It also sends a delay warning after four hours.

Rspamd and ClamAV inspect content before final acceptance. A scanner failure causes a temporary SMTP failure, so the sending server must retry.

Malware and prohibited encrypted content cause a permanent SMTP rejection. The server does not put rejected content in a user mailbox.

## Security boundary

No connected server can provide a mathematical guarantee against every future defect. This design reduces damage from unknown mail content through independent controls.

The server never runs message content or attachments. Mail files use the unprivileged `vmail` identity, which has no login shell.

The `/var/vmail` mount uses `nosuid`, `nodev`, and `noexec`. These flags prevent direct execution and remove device and set-user-ID behavior.

ClamAV runs with AppArmor enforcement and a restricted systemd unit. Dovecot, Rspamd, Valkey, and the administrator application use separate sandboxes.

The administrator application can connect only to loopback PostgreSQL. Its unit blocks all other network destinations and removes all Linux capabilities.

Nginx binds the administrator site only to `192.168.89.251:8443`. Nftables also permits that port only from `192.168.89.0/24`.

Do not add an administrator DNS record. Never forward TCP port 8443 through RouterOS.

Nftables drops unmatched input. SSH accepts keys only and permits access from the local network only.

PostgreSQL, Valkey, Rspamd, ClamAV, and the .NET backend listen only on loopback or local Unix sockets.

Fail2ban protects SSH, Postfix, Dovecot, and nginx. Unattended security updates install automatically, with a controlled reboot window.

The host boots to `multi-user.target`. SDDM stays disabled, so the installed KDE packages do not start a graphical login.

Avahi, Bluetooth, CUPS, and KDE crash processing are masked. Persistent core images and automated crash stack processing are disabled.

AIDE ignores boot-generated device links and private temporary directories. It continues to monitor configuration, code, units, keys, and packages.

## Prepared target

The target uses Debian 13 at `192.168.89.251`. Its measured public IPv4 address is `176.61.153.171`.

The host runs .NET runtime 10.0.11 and PostgreSQL 17. The production host does not contain a .NET SDK.

KDE dependencies retain Git and GNU compiler binaries. The host has no source checkout or build job.

The host also requires `s-nail` for local AIDE alert delivery. A deployment stops before changes when this command is absent.

The host has no global IPv6 address. Do not publish an AAAA record before a complete IPv6 path passes tests.

The prerequisite manifest records each required production package. The verifier accepts only Debian 13 on amd64.

The verifier compares all active APT sources with the reviewed source manifest. It also checks repository package files and signing-key fingerprints.

The [Microsoft Debian instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian) define the .NET 10 feed and runtime package.

The [Synapse installation guide](https://element-hq.github.io/synapse/latest/setup/installation.html) defines the Matrix package feed and published key fingerprint.

The verifier rejects missing packages, held packages, incomplete package operations, and available package updates. It also rejects a .NET SDK on production.

Run the installed verifier before controlled maintenance. Production deployment first refreshes signed APT metadata.

Deployment runs the verifier before any backup, service, configuration, or release change.

```sh
sudo verify-host-prerequisites
sudo /usr/local/lib/mk8email/tests/prerequisites_smoke
```

The database contains active `admin@mk8n.com` and `mk8n@mk8n.com` accounts. The `admin` account has the `SuperAdmin` role.

The `mk8n.com` catch-all route sends undefined local addresses to `mk8n@mk8n.com`. An exact address always has priority.

The workstation stores account credentials below `D:\keys\shitbox1\services`. Windows permits access only to the current user and `SYSTEM`.

## Continuous integration and delivery

GitHub Actions restores locked NuGet dependencies on Ubuntu 24.04. It treats compiler warnings as errors and runs both test projects.

The workflow also checks shell scripts and Python test programs. It publishes the management command and administrator application into a checksummed artifact.

Dependabot checks NuGet packages and GitHub Actions each week. Merge an update only after CI and a local production test pass.

The production deployment has an operator gate because a public mail server must not deploy an unreviewed commit automatically.

Run this command from the repository root on the authorized workstation.

```powershell
pwsh -NoProfile -File deploy\scripts\deploy-production.ps1 -Activate
```

The deployment uses locked restore, a warning-free build, all tests, and fresh publish output. It stores all temporary files below `D:\temp\mk8.email`.

The release identifier covers both application files and deployment assets. The target stores immutable releases below `/opt/mk8email/releases`.

Deployment protects the prior release during pruning. It keeps the active release, the protected release, and one additional rollback release.

Pruning validates every release path before deletion. It stops without deletion when the directory contains an unknown name or unsafe path.

An isolated mount-namespace test verifies protection, validation, pruning order, and the three-release limit during each active deployment.

An active deployment first creates a local snapshot and encrypted export. It installs and validates the new files before service activation.

The activation gate checks configurations, services, PostgreSQL access, ClamAV, Valkey, the dashboard, timers, and deep health status.

The gate also runs the restricted systemd health unit. This check detects errors hidden by a direct root command.

Deployment resets an AIDE result only when deployment interrupts an active scan. It preserves an unrelated integrity-service failure.

Any failed deep health check fails activation. The deployment then restores the prior release and configuration.

If installation fails, the deployment restores the prior configuration and release link. It then starts the prior mail stack.

## Database lifecycle

The current initial schema was created on the empty target with the application initialization command. Project policy prohibited migration generation without explicit approval.

Future schema changes require an explicit Entity Framework migration. Review its SQL and restore test before production use.

Create a new backup before each schema change. Apply the change during a maintenance window with a tested rollback point.

Use the local administrator dashboard for normal domains and accounts. Use the management command only for recovery or controlled automation.

The root-only `mk8email` command supplies the production configuration. It exposes only health, domain, account, and catch-all operations.

Invalid commands exit before configuration loading. Managed failures return a concise error without an unhandled stack trace or core image.

```sh
mk8email --healthcheck
mk8email --ensure-domain mk8n.com mk8n
mk8email --create-account user@mk8n.com User /root/protected-password-file
mk8email --set-catchall mk8n.com mk8n@mk8n.com
```

Never pass a password as a command argument. Place it in a root-readable file and remove that file after the command succeeds.

## Trusted certificate

The installed certificate is temporary and self-signed. Keep public mail closed until a trusted certificate is active.

Apply only the RouterOS web preflight rules first. Import `deploy/dns/mk8n.com-preflight.zone` after you verify the public address.

Issue the mail certificate after all three names reach this nginx service from the Internet.

```sh
certbot certonly --webroot --webroot-path /var/www/letsencrypt --cert-name mk8-mail --deploy-hook '/usr/local/sbin/deploy-mk8-certificate mail' -d mail.mk8n.com -d mta-sts.mk8n.com -d autoconfig.mk8n.com
```

Run the deployment hook after the first issue. Check the certificate names and expiration before full activation.

The hook rejects a certificate that lacks any required service name. It also rejects an expired, mismatched, or incomplete certificate.

```sh
/usr/local/sbin/deploy-mk8-certificate mail
certbot renew --dry-run
```

## Public activation gates

Do not import the final MX records until every gate in this section passes.

RouterOS must forward only the documented public ports. The Cloudflare records must use DNS-only mode.

The ISP must permit inbound and outbound TCP port 25. The target currently cannot connect to remote TCP port 25.

The ISP must set PTR `176.61.153.171` to `mail.mk8n.com`. The forward `mail.mk8n.com` record must return the same address.

The trusted certificate must cover `mail.mk8n.com`, `mta-sts.mk8n.com`, and `autoconfig.mk8n.com`.

External tests must pass for SMTP, submission, IMAPS, DKIM, SPF, DMARC, MTA-STS, and TLS reporting.

The final MTA-STS policy uses `mode: enforce`. Publish it only after the HTTPS endpoint and certificate pass external validation.

Use the [RouterOS and DNS guide](routeros-and-dns.md) for the exact cutover order. Keep an encrypted off-host backup before the change.

## Measured local evidence

The release build completed with zero warnings and zero errors. Forty application and infrastructure tests passed.

Live tests passed for exact delivery, catch-all delivery, authenticated submission, IMAPS retrieval, DKIM signing, and relay denial.

Live tests also passed for sender mismatch denial and TLS version limits. TLS 1.0 failed, while TLS 1.2 and TLS 1.3 succeeded.

EICAR, password-protected EICAR archives, and GTUBE content received permanent rejections. A disabled scanner caused a temporary, fail-closed rejection.

A forced Dovecot outage kept the accepted message in the Postfix queue. Delivery completed after Dovecot returned, and the queue became empty.

The dashboard passed authentication, authorization, antiforgery, secure-cookie, account, and network tests. The backend port was not reachable from the LAN.

A cold reboot returned all required units without failed services. Deep health checks and all live mail tests passed after that reboot.

The reboot check also confirmed the headless boot target, disabled SDDM, masked unused services, and disabled persistent core dumps.

The encrypted backup passed checksum, decryption, database restore, account, catch-all, configuration, and ownership tests on a separate path.

## Routine operations

Use [the mail operations guide](mail-operations.md) for account work, health checks, logs, backups, updates, incidents, and restores.

Keep Matrix Synapse disabled until its source database, media, configuration, and original signing key pass migration validation.
