# RouterOS and DNS deployment

## Assumptions

The server address is `192.168.89.251`. The measured public IPv4 address is `176.61.153.171`.

The RouterOS configuration must have an interface list named `WAN`. Change the script if your interface list uses another name.

Confirm that the public address is static. Dynamic or shared carrier-grade NAT service is not suitable for direct mail delivery.

Reserve `192.168.89.251` for the server. Inspect the active lease before you make it static.

```routeros
/ip dhcp-server lease print detail where active-address=192.168.89.251
/ip dhcp-server lease make-static [find where active-address=192.168.89.251]
```

If the host uses a manual address, exclude that address from the DHCP pool instead.

## RouterOS backup

Create a RouterOS export before any change.

```routeros
/export show-sensitive=no file=before-mk8-services
/system backup save name=before-mk8-services
```

Download both files from the router. Keep them outside the router.

## RouterOS rules

Use `deploy/routeros/mk8-web-preflight.rsc` before certificate issuance. It forwards only TCP ports 80 and 443.

Confirm the router has a populated `WAN` interface list before import.

```routeros
/interface/list/member/print where list=WAN
/ip/address/print
```

The scripts replace only rules with their exact comments. You can import them again after a reviewed change.

Use RouterOS 7.16 or later to run the import syntax check without changes.

```routeros
/import file-name=mk8-web-preflight.rsc verbose=yes dry-run=yes
```

Apply the preflight file only after the dry run succeeds.

```routeros
/import file-name=mk8-web-preflight.rsc
```

Import `deploy/routeros/mk8-public-services.rsc` only after every mail activation gate passes.

Run the same dry-run command with `mk8-public-services.rsc`. Then import that file.

The full script removes the preflight rules. It forwards only required mail, web, Matrix, and TURN ports.

Keep TCP ports 22 and 5432 closed on the WAN. Never create a general port-forward rule for this host.

Inspect the new counters after each external test.

```routeros
/ip firewall nat print stats where comment~"mk8"
/ip firewall filter print stats where comment~"mk8"
```

The full script permits outbound TCP port 25 from the host. A timeout after import means the ISP probably blocks SMTP.

Ask the ISP for inbound and outbound TCP port 25. Ask the ISP to set PTR `176.61.153.171` to `mail.mk8n.com`.

The forward `mail.mk8n.com` address must return the same public address. Do not publish the MX record before both directions match.

## Cloudflare records

Import `deploy/dns/mk8n.com-preflight.zone` first. It creates only the address records needed for mail certificate issuance.

Import `deploy/dns/mk8n.com.zone` only during mail cutover. It adds MX, sender policy, discovery, and reporting records.

Import the Matrix fragment into the `mkn8rn.com` zone only during Matrix cutover.

Keep all mail, MTA-STS, autoconfiguration, Matrix, and TURN address records in DNS-only mode. Cloudflare proxy mode must stay off.

Clear the Cloudflare `Proxy imported DNS records` option during each import. The address files also enforce the `cf-proxied:false` tag.

The files contain the measured public address. Replace it before import if the router address changed.

The DKIM record matches the protected selector key on the target. Do not change its selector or public-key text.

The mail zone also publishes IMAPS, submission, and implicit-submission SRV records.

The initial DMARC policy is monitoring mode. The initial MTA-STS policy is testing mode.

## External checks

Test the preflight TCP ports 80 and 443 from a connection outside the local network. A mobile connection is sufficient.

Issue the mail certificate after the preflight records reach nginx from the Internet. Confirm automatic certificate renewal with a dry run.

```sh
certbot renew --dry-run
```

After the full import, confirm TCP ports 25, 465, 587, 993, and 3478. Confirm UDP port 3478 and the TURN relay range.

Do not expose a port that has no active service. Remove its RouterOS rule until the service is ready.

Import the mail cutover zone last. Keep the old Matrix server active until the migration validation passes.
