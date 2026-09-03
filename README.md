# mk8.email

A work-in-progress self-hosted email server built with .NET 10 and designed for Thunderbird compatibility.

The host foundation is hardened, but the application is not ready for public MX traffic. Read the production boundary before activation.

The server requires the `MK8EMAIL_CONFIG_FILE` environment variable. The variable must contain an absolute JSON configuration file path.

The repository includes native Debian, host-hardening, network, and container assets for `mail.mk8n.com`.

Read [the production deployment guide](docs/production-deployment.md) before use. Read [the network guide](docs/routeros-and-dns.md) before changing public DNS or RouterOS.

The same Debian host is a locked migration target for `matrix.mkn8rn.com`. Read [the Matrix migration guide](docs/matrix-migration.md) before any cutover.
