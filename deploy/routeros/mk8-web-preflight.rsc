/ip firewall nat
remove [find where comment="mk8 preflight TCP web"]
add chain=dstnat action=dst-nat in-interface-list=WAN dst-address-type=local protocol=tcp dst-port=80,443 to-addresses=192.0.2.251 comment="mk8 preflight TCP web" place-before=0

/ip firewall filter
remove [find where comment="allow mk8 preflight TCP web"]
add chain=forward action=accept in-interface-list=WAN connection-nat-state=dstnat connection-state=new protocol=tcp dst-address=192.0.2.251 dst-port=80,443 comment="allow mk8 preflight TCP web" place-before=0
