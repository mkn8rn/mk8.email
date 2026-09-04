# Matrix Synapse migration

## Fixed identity

The existing server name is `matrix.mkn8rn.com`. This value cannot change during migration.

The live server reported Synapse 1.144.0. Its current signing-key identifier is `ed25519:a_sSJi`.

The expected public key is `k9qMy4VtXskSmpACoaVuZKa8tvwK+UJX9YuBoOIwuN0`.

The target has Synapse 1.160.0. A systemd condition keeps the target service off until `/etc/matrix-synapse/migration-ready` exists.

Do not create that marker before the signing key, database, media store, and private configuration arrive.

The target does not have source-server access. Migration cannot continue until an operator supplies access or a complete verified source bundle.

## Prepared target state

The `synapse` PostgreSQL role can connect only to its local database through SCRAM authentication. PostgreSQL listens on loopback addresses only.

The target Synapse database has no application tables. This empty state is required before the source restore.

The target configuration selects PostgreSQL and a loopback HTTP listener on TCP port 8008. Nginx is the only planned public HTTP path.

Public registration and URL previews stay disabled. Coturn is active with a protected shared secret and a restricted relay range.

The generated new-install signing key is parked with an `UNUSED-new-install` name. It must never replace the source signing key.

Synapse stays disabled and inactive. The migration marker is absent, and the loopback port 8008 is closed.

## Source data

Preserve the complete Synapse configuration directory. Preserve each referenced application-service file and module configuration.

Preserve the active server signing key. A missing key can break federation trust and old event verification.

Preserve the media store. Local uploads can exist only in that store.

Preserve the database with a consistent final snapshot. Preserve the existing macaroon and form secrets from the configuration.

## PostgreSQL source

Inspect the source database setting first. Stop Synapse before the final snapshot.

Create a custom PostgreSQL dump. Exclude one-time encryption-key data as the Synapse backup guide requires.

```sh
sudo systemctl stop matrix-synapse
sudo -u postgres pg_dump -Fc --exclude-table-data=e2e_one_time_keys_json synapse > synapse.dump
```

Archive the configuration, signing key, and media store. Keep their ownership information.

```sh
sudo tar --create --gzip --file=synapse-files.tar.gz /etc/matrix-synapse /var/lib/matrix-synapse/media
sha256sum synapse.dump synapse-files.tar.gz > SHA256SUMS
```

Keep the source stopped after the final snapshot. Restart it only for a controlled rollback before target writes occur.

## SQLite source

Use `synapse_port_db` when the source uses SQLite. The official tool supports repeated snapshots before the final stop.

Never run `VACUUM` between repeated conversion runs. That operation can make the conversion inconsistent.

Follow the [official PostgreSQL port guide](https://element-hq.github.io/synapse/latest/postgres.html#porting-from-sqlite) exactly.

## Target restore

Verify the transfer checksums before extraction. Keep the target service locked.

Restore configuration files carefully. Keep the target PostgreSQL and listener overlay as the final configuration file.

Restore the old signing key as `/etc/matrix-synapse/homeserver.signing.key`. Set owner and group to `matrix-synapse`.

Restore media below `/var/lib/matrix-synapse/media`. Set its owner and group to `matrix-synapse`.

The target `synapse` database must be empty before restore. Never restore over existing Synapse tables.

```sh
sudo -u postgres dropdb synapse
sudo -u postgres createdb --encoding=UTF8 --locale=C --template=template0 --owner=synapse synapse
sudo -u postgres pg_restore --exit-on-error --no-owner --role=synapse --dbname=synapse synapse.dump
```

Run the database updater before public activation. Wait for all schema work and background updates.

Review every upgrade note from 1.145.0 through 1.160.0. Version 1.157.0 removes experimental MSC3861 authentication delegation.

## Matrix certificate

Matrix has a separate certificate path. This design lets the old server keep the `matrix.mkn8rn.com` record during preparation.

Issue the target certificate after the final DNS switch makes HTTP reach the target nginx service.

```sh
certbot certonly --webroot --webroot-path /var/www/letsencrypt --cert-name mk8-matrix --deploy-hook '/usr/local/sbin/deploy-mk8-certificate matrix' -d matrix.mkn8rn.com
```

Use a Cloudflare DNS challenge if you must issue this certificate before cutover. Keep the restricted API token outside Git.

## Target validation

Export the restored signing key before service start. Its identifier and public value must match the current live key.

Validate the complete Synapse configuration. Confirm the listener uses loopback port 8008 only.

Take a target database backup before the first 1.160.0 start. This save point is required for rollback.

Create `/etc/matrix-synapse/migration-ready` after every prior check passes. Enable and start Synapse.

Test client versions through nginx. Test federation version and server keys through nginx.

Confirm login, room history, media, sending, receiving, and federation with controlled accounts. Confirm TURN relay allocation separately.

Change `matrix.mkn8rn.com` DNS only after local validation passes. Keep the DNS record in DNS-only mode.

Do not run the old and new servers concurrently with the same identity. Concurrent writers can split database and federation state.

Run `mk8-backup` after final validation and before public DNS changes. Pull its encrypted export to the workstation.

Keep the old server stopped and unchanged until the target completes a stable observation period. Never use both systems as writers.

The [official Synapse backup guide](https://element-hq.github.io/synapse/latest/usage/administration/backups.html) defines the protected data set.
