# Production deployment foundation

## Current boundary

Do not route `mk8n.com` mail to this service yet. Database migrations and standards-compliant mail authentication remain required.

This foundation provides strict configuration, secret files, non-root execution, isolated PostgreSQL networking, TLS ports, and a container health check.

## Host requirements

Use a Linux host with a static public IPv4 address. Confirm that the provider permits inbound and outbound TCP port 25.

Set the host name to `mail.mk8n.com`. Configure the firewall for TCP ports 25, 465, 587, 143, and 993.

Request a PTR record that maps the public address to `mail.mk8n.com`. The forward address must map to the same public address.

## Local secrets

Create the ignored `deploy/secrets` directory. Store each secret in its named file without an additional line.

```sh
mkdir -p deploy/secrets
openssl rand -base64 48 > deploy/secrets/database_password
openssl rand -base64 48 > deploy/secrets/superadmin_password
```

Copy the complete certificate chain to `deploy/secrets/tls_certificate.pem`. Copy its private key to `deploy/secrets/tls_private_key.pem`.

Restrict all secret files to the deployment administrator. Do not add these files to Git.

## Container checks

Validate the Compose model before each deployment.

```sh
docker compose config --quiet
```

Build the image with current base-image security updates.

```sh
docker compose build --pull
```

Start the services only after all remaining blockers are complete.

```sh
docker compose up -d
docker compose ps
```

The mail container runs without root privileges. It binds high internal ports, while Docker publishes the standard mail ports.

The database is only attached to the internal backend network. The mail container also uses an edge network for remote delivery.

## DNS preparation

Replace `203.0.113.10` with the server address. Create equivalent records at the authoritative DNS provider.

```dns
mail.mk8n.com. 300 IN A 203.0.113.10
mk8n.com. 300 IN MX 10 mail.mk8n.com.
mk8n.com. 300 IN TXT "v=spf1 mx -all"
_dmarc.mk8n.com. 300 IN TXT "v=DMARC1; p=none; rua=mailto:dmarc@mk8n.com"
```

Do not publish a DKIM selector until the server has standards-compliant signing. Change the DMARC policy only after measured mail-flow results.

## Operations

Back up the PostgreSQL volume each day. Test database restoration before mail delivery starts.

Monitor connection failures, authentication failures, rejected messages, queue failures, disk use, certificate expiry, and database health.

Replace renewed TLS files and recreate the mail container. Confirm STARTTLS and implicit TLS after each certificate change.

## Remaining production blockers

The repository contains no Entity Framework migrations. The user must explicitly authorize migration creation before database deployment can work.

Outbound delivery does not perform MX lookup. Inbound SPF, DKIM, and DMARC checks are not standards-compliant and stay disabled.

Transport tests cover SMTP and IMAP greetings, STARTTLS, authentication exposure, and SMTP size limits. Broader mailbox interoperability still needs automated tests.

External deliverability tests must confirm behavior before the DNS cutover.
