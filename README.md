# mk8.email

A work in progress self-hosted email server built with .NET 10 designed for Thunderbird compatibility.

The server requires the `MK8EMAIL_CONFIG_FILE` environment variable. The variable must contain an absolute JSON configuration file path.

The repository includes a hardened container foundation for `mail.mk8n.com`. Read [the production deployment guide](docs/production-deployment.md) before use.
