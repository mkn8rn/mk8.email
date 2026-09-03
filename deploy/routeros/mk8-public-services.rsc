/ip firewall nat
remove [find where comment="mk8 preflight TCP web"]
remove [find where comment="mk8 public TCP services"]
remove [find where comment="mk8 TURN UDP services"]
add chain=dstnat action=dst-nat in-interface-list=WAN dst-address-type=local protocol=tcp dst-port=25,80,443,465,587,993,3478 to-addresses=192.168.89.251 comment="mk8 public TCP services" place-before=0
add chain=dstnat action=dst-nat in-interface-list=WAN dst-address-type=local protocol=udp dst-port=3478,49160-49200 to-addresses=192.168.89.251 comment="mk8 TURN UDP services" place-before=0

/ip firewall filter
remove [find where comment="allow mk8 preflight TCP web"]
remove [find where comment="allow mk8 public TCP services"]
remove [find where comment="allow mk8 TURN UDP services"]
remove [find where comment="allow mk8 outbound SMTP"]
add chain=forward action=accept in-interface-list=WAN connection-nat-state=dstnat connection-state=new protocol=tcp dst-address=192.168.89.251 dst-port=25,80,443,465,587,993,3478 comment="allow mk8 public TCP services" place-before=0
add chain=forward action=accept in-interface-list=WAN connection-nat-state=dstnat connection-state=new protocol=udp dst-address=192.168.89.251 dst-port=3478,49160-49200 comment="allow mk8 TURN UDP services" place-before=0
add chain=forward action=accept out-interface-list=WAN connection-state=new protocol=tcp src-address=192.168.89.251 dst-port=25 comment="allow mk8 outbound SMTP" place-before=0
