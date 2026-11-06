# AMBE channel FEC and hardware transport

Import `Ambe.props` to compile these internal helpers into a plugin. There is
no shared runtime assembly or dependency on another plugin. The import also
adds System.IO.Ports and the accompanying notices and license texts.

`AmbeChannelFec` converts 72 coded bits to 49 parameter bits in managed code.
`AmbeDongleDecoder` sends those parameters to an external AMBE-3000 dongle and
reads PCM, either directly over COM or through AMBEserver over UDP.
Neither helper performs software speech synthesis.

## AMBEserver connection

The ARIB T98 receiver accepts `COM6` or
`udp://192.168.1.10:2460` in its **AMBE接続先** field. Hostnames and IPv6
literals (for example `udp://[::1]:2460`) are also accepted. The default UDP
port is 2460 when omitted. An empty endpoint disables audio decoding.
The existing `AmbePort` JSON setting stores either form, so existing COM
settings remain valid. Baud rate applies only to COM connections.

The UDP transport follows the [NW Digital Radio AMBEserver packet protocol](https://github.com/nwdigitalradio/ambeserver-install/blob/master/usr/local/src/AMBEserver/AMBEserver.c):
one complete DVSI packet per datagram. It probes product identification and
configures the rate, linear PCM, and decoder initialization.

A receive timeout (500 ms), malformed response or rejected command stops
audio and reports the error. For T98, changing the endpoint or disabling and
re-enabling the plugin resets the connection. AMBEserver supports one
application stream at a time; use a dedicated endpoint/device for each active
voice stream.

## Native analysis

T98 additionally maintains its own native soft FEC and parameter analysis for
privacy analysis. Other plugins do not need those native DLLs.

See [third-party notices](THIRD-PARTY-NOTICES.md). Preserve
`AMBE.THIRD-PARTY-NOTICES.md`, `licenses/ambe` and the System.IO.Ports notice
when distributing a plugin that imports these sources.
