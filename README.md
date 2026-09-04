# mk8.email

mk8.email is the mail control plane for a native Debian mail server. The production data plane uses standard, maintained mail components.

Postfix receives and queues SMTP mail. Rspamd and ClamAV inspect messages before acceptance. Dovecot provides LMTP delivery and IMAPS access.

PostgreSQL stores domains, accounts, password hashes, quotas, and aliases. The .NET 10 Razor Pages application provides the local administrator interface.

The original custom .NET SMTP and IMAP servers remain available only through an explicit experimental command. Production services do not use them.

The prepared target is Debian 13 at `192.168.89.251`. The administrator dashboard listens only at `https://192.168.89.251:8443` on the local network.

Read [the production deployment guide](docs/production-deployment.md) before a release. Read [the mail operations guide](docs/mail-operations.md) for routine work and recovery.

Read [the RouterOS and DNS guide](docs/routeros-and-dns.md) before public cutover. Read [the Matrix migration guide](docs/matrix-migration.md) before moving Synapse.

Public mail is not active until RouterOS, public DNS, PTR, a trusted certificate, and unrestricted outbound TCP port 25 pass external tests.
