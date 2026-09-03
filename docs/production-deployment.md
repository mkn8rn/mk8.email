# Production deployment

## Deployment boundary

Do not change the `mk8n.com` MX record yet. Several conditions still block live mail delivery.

The Internet path from the target blocks outbound TCP port 25. The public address has no usable PTR result. The application database has no schema.

The repository has no Entity Framework migration. Project rules require an explicit user instruction before migration generation.

Inbound SPF, DKIM, and DMARC evaluation is not implemented. Durable outbound retry and bounce queues are not implemented.

Spam classification and malware scanning are not implemented. Do not direct public MX traffic to this service until these controls exist.

The API has no controllers or administrative endpoints. The service cannot provision a domain or inbox through a supported interface.

## Target host

The prepared host uses Debian 13 at `192.168.89.251`. Its current public IPv4 address is `176.61.153.171`.

Nftables drops unmatched input. SSH permits public-key access from `192.168.89.0/24` only. PostgreSQL listens on loopback addresses only.

The public service rules permit IPv4 only. Do not publish an AAAA record before you add and test a complete IPv6 path.

Nginx owns TCP ports 80 and 443. The mail service will own TCP ports 25, 465, 587, and 993.

The host has .NET 10.0.11 and PostgreSQL 17. PostgreSQL uses SCRAM authentication and page checksums.

The mail service is installed from commit `caabd4e169a1`. A systemd condition keeps it stopped until the database schema is approved.

## Host hardening assets

The versioned hardening files reproduce the target security settings. Install their required Debian packages before you run the installer.

```sh
apt-get install apparmor apparmor-utils auditd fail2ban nftables unattended-upgrades needrestart debsums aide aide-common acct sysstat libpam-pwquality libpam-tmpdir
deploy/scripts/install-host-hardening /path/to/mk8.email
reboot
```

The installer permits SSH only from `192.168.89.0/24`. Keep a working console or tested key session during this operation.

The nftables asset replaces the complete host ruleset. Review it before use on a host that runs another network service.

Configure NetworkManager with the router and two independent public resolvers. The prepared host uses `192.168.89.1`, `1.1.1.1`, and `9.9.9.9`.

```sh
nmcli connection modify 'Wired connection 1' ipv4.ignore-auto-dns yes ipv4.dns '192.168.89.1,1.1.1.1,9.9.9.9' ipv6.ignore-auto-dns yes
nmcli device reapply enp0s31f6
```

Initialize AIDE after the final trusted configuration is present.

```sh
aideinit --yes --force
```

The measured Lynis hardening index is 82. The scan had no warning-level findings after the applied corrections.

Review .NET and Synapse updates each week. Apply those third-party updates during a tested maintenance window.

## Protected values

The workstation stores host and service values below `D:\keys\shitbox1`. Windows permits access only to the current user and `SYSTEM`.

The target stores mail values below `/etc/mk8email`. Files with private values use the `root:mk8email` owner and group.

Never add a private value to Git. Never copy a private value into a command argument.

## Native release

Build from a clean worktree. Use locked NuGet restore before tests.

```powershell
dotnet restore mk8.email.slnx --locked-mode
dotnet test mk8.email.slnx --configuration Release --no-restore
dotnet publish mk8.email.CLI\mk8.email.Application.CLI.csproj --configuration Release --no-restore --output publish
```

Create a release archive from the publish directory. Transfer the archive through the dedicated SSH key.

Run the installer with an immutable Git commit identifier.

```sh
install-native-release /root/mk8email-release.tar.gz caabd4e169a1
```

Install `deploy/native/mk8email.config.json` as `/etc/mk8email/mk8email.config.json`. Set ownership to `root:mk8email` and mode `0640`.

Install `deploy/systemd/mk8email.service` as `/etc/systemd/system/mk8email.service`. Reload systemd after installation.

The unit requires `/etc/mk8email/schema-ready`. Do not create this file before the first approved database migration exists.

## Database initialization

The hardening installer installs the PostgreSQL tuning and access-control files. It keeps PostgreSQL on loopback addresses.

Initialize the two restricted roles and databases from protected password files.

```sh
deploy/scripts/initialize-postgresql /etc/mk8email/secrets/database_password /etc/matrix-synapse/secrets/database_password
```

This script enables PostgreSQL page checksums before it creates data. It does not create the application schema.

Generate an Entity Framework migration only after explicit approval. Review its SQL before application.

Create a database backup before each schema change. Apply the migration during a measured maintenance window.

Create `/etc/mk8email/schema-ready` only after the schema validation passes. Start the service after that marker exists.

The first start creates only the `postmaster@mk8n.com` user row. Its password is in the protected workstation directory.

Implement and test an authenticated administration path before activation. Then provision the domain and all required inboxes through that path.

Create the `postmaster@mk8n.com`, `dmarc@mk8n.com`, and `tlsrpt@mk8n.com` inboxes before the DNS cutover.

## TLS certificate

The installed certificate is temporary and self-signed. Do not use it for public mail.

Apply the RouterOS preflight rules first. Import `deploy/dns/mk8n.com-preflight.zone` after external HTTP and HTTPS checks pass.

The preflight zone adds only three address records. It does not change mail routing or sender policy.

Issue the mail certificate after the three `mk8n.com` names resolve to this host.

```sh
certbot certonly --webroot --webroot-path /var/www/letsencrypt --cert-name mk8-mail --deploy-hook '/usr/local/sbin/deploy-mk8-certificate mail' -d mail.mk8n.com -d mta-sts.mk8n.com -d autoconfig.mk8n.com
```

Run the deployment hook once after the first issuance. Confirm certificate names and expiry before service activation.

Matrix uses a separate certificate and deployment hook. This separation keeps the current Matrix DNS record on the source during preparation.

## Backups

The `mk8-backup.timer` creates a local backup each day. It keeps 14 days of database, configuration, and Matrix media snapshots.

Local backups do not protect against host loss. Copy each completed snapshot to a different system.

Each snapshot contains database credentials, signing keys, and TLS keys. Encrypt off-host copies and restrict their access.

Test restoration each month. Keep the test separate from the live databases.

## Activation tests

Run `systemctl status mk8email` after activation. Run the built-in health check with the production configuration.

```sh
sudo -u mk8email env MK8EMAIL_CONFIG_FILE=/etc/mk8email/mk8email.config.json /usr/bin/dotnet /opt/mk8email/current/mk8.email.Application.CLI.dll --healthcheck
```

Test SMTP STARTTLS on ports 25 and 587. Test implicit TLS on ports 465 and 993.

Send mail only between controlled test accounts first. Confirm sender authorization and DKIM validation.

Test delivery to three independent providers. Confirm that each provider accepts the message and validates SPF, DKIM, and DMARC.

Keep DMARC in monitoring mode during initial tests. Change MTA-STS from `testing` only after stable mail-flow evidence exists.
