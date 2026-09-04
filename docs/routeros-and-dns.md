# RouterOS and DNS deployment

## Current public blockers

The server address is `192.168.89.251`. The last measured public IPv4 address is `176.61.153.171`.

The public address does not have the required PTR record. The ISP path also blocks outbound TCP port 25.

The installed mail certificate is temporary and self-signed. Current `mk8n.com` MX records still use Cloudflare Email Routing.

Do not change public mail routing until the ISP, certificate, RouterOS, and DNS conditions in this guide pass.

## Router assumptions

The RouterOS configuration must have an interface list named `WAN`. Change the versioned scripts if your router uses another name.

Confirm that the public address is static. Shared carrier-grade NAT service cannot support a direct public mail server.

Reserve `192.168.89.251` for the server. Inspect its active lease before you make the lease static.

```routeros
/ip dhcp-server lease print detail where active-address=192.168.89.251
/ip dhcp-server lease make-static [find where active-address=192.168.89.251]
```

Exclude `192.168.89.251` from the DHCP pool if the host uses a manual address.

Do not configure IPv6 forwarding for this host. The server has no tested global IPv6 path.

## Router save point

Create a RouterOS export and binary backup before every import.

```routeros
/export show-sensitive=no file=before-mk8-services
/system backup save name=before-mk8-services
```

Download both files from the router. Keep the copies outside the router.

Confirm that the `WAN` list contains the public interface. Confirm the router owns the expected public address.

```routeros
/interface/list/member/print where list=WAN
/ip/address/print
```

## Web preflight

Use `deploy/routeros/mk8-web-preflight.rsc` before certificate issuance. It forwards only TCP ports 80 and 443.

RouterOS 7.16 or later can validate an import without changes. Run the dry check first.

```routeros
/import file-name=mk8-web-preflight.rsc verbose=yes dry-run=yes
```

Apply the file only after the dry check succeeds.

```routeros
/import file-name=mk8-web-preflight.rsc
```

Import `deploy/dns/mk8n.com-preflight.zone` into Cloudflare. Keep all three address records in DNS-only mode.

Test TCP ports 80 and 443 from a connection outside the local network. Confirm that each name reaches this nginx instance.

Issue the trusted mail certificate only after the external HTTP test passes. Then run `certbot renew --dry-run`.

## Full RouterOS rules

Use `deploy/routeros/mk8-public-services.rsc` only after the local mail tests and certificate tests pass.

Run the same dry check with the full file. Apply it only after you review the exact diff.

The full script replaces only rules with its exact comments. It removes the temporary web rules during installation.

The script forwards TCP ports 25, 80, 443, 465, 587, 993, and 3478. It forwards UDP port 3478 and ports 49160 through 49200.

The UDP range supports TURN. Keep Matrix DNS on the old server until its separate migration validation passes.

The script permits only outbound TCP port 25 through its new mail rule. Existing general client traffic remains subject to your current router policy.

Never forward TCP ports 22, 5432, 6379, 8008, 8080, 8443, 11332, or 11333.

TCP port 8443 is the local administrator dashboard. Both RouterOS and the host firewall must keep it private.

Inspect counters after every external test.

```routeros
/ip firewall nat print stats where comment~"mk8"
/ip firewall filter print stats where comment~"mk8"
```

## ISP requirements

Ask the ISP to permit inbound and outbound TCP port 25 for `176.61.153.171`.

Ask the ISP to set PTR `176.61.153.171` to `mail.mk8n.com`. Cloudflare cannot configure this record.

The forward `mail.mk8n.com` record must return `176.61.153.171`. Forward and reverse results must agree before MX cutover.

Test outbound port 25 from the server after the ISP change. A timeout still indicates a network block.

```sh
timeout 10 nc -vz gmail-smtp-in.l.google.com 25
```

## Cloudflare mail cutover

Export the existing Cloudflare zone before any change. Keep that file as the DNS rollback point.

Disable Cloudflare Email Routing for `mk8n.com` during the final cutover. Remove its three `route*.mx.cloudflare.net` MX records.

Remove the old Cloudflare Email Routing SPF record. Only one SPF record can remain at the zone apex.

Keep the three address records from `deploy/dns/mk8n.com-preflight.zone`. Import `deploy/dns/mk8n.com.zone` after conflict removal.

Clear Cloudflare's proxy option during each import. Every mail address record must stay in DNS-only mode.

The final zone fragment adds the direct MX, SPF, DKIM, DMARC, MTA-STS, TLS reporting, and client discovery records.

The DKIM record matches the protected target key. Do not edit its selector or public value.

The first DMARC policy uses monitoring mode. Review reports before you change `p=none` to an enforcement policy.

The MTA-STS policy uses enforcement mode with a seven-day maximum age. Publish it only after trusted HTTPS tests pass.

Do not publish an AAAA record. Do not create a public record for `admin.mk8n.com`.

## External mail checks

Query two independent public resolvers after the import. Confirm the direct MX, address, SPF, DKIM, DMARC, and MTA-STS identifiers.

```sh
dig @1.1.1.1 mk8n.com MX
dig @9.9.9.9 mail.mk8n.com A
dig @1.1.1.1 mk8n.com TXT
dig @9.9.9.9 s202609._domainkey.mk8n.com TXT
dig @1.1.1.1 _dmarc.mk8n.com TXT
dig @9.9.9.9 _mta-sts.mk8n.com TXT
```

Fetch the MTA-STS policy through trusted HTTPS. Confirm that it names only `mail.mk8n.com`.

```sh
curl --fail https://mta-sts.mk8n.com/.well-known/mta-sts.txt
```

Test public TCP ports 25, 465, 587, and 993. Confirm valid certificate chains and TLS 1.2 or TLS 1.3.

Send controlled mail to and from at least three independent providers. Confirm SPF, DKIM, DMARC, queue, and bounce behavior.

Keep the former routing data available for rollback. Do not restore Cloudflare Email Routing after this server accepts new local mail.

## Matrix DNS

`matrix.mkn8rn.com` still points to the existing server. Do not import `deploy/dns/mkn8rn.com-matrix.zone` during the mail cutover.

Complete the [Matrix migration guide](matrix-migration.md) first. Change Matrix and TURN records only during its final stopped-source migration.
