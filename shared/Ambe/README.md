# AMBE channel FEC and hardware transport

Import `Ambe.props` to compile these internal helpers into a plugin. There is
no shared runtime assembly or dependency on another plugin. The import also
adds System.IO.Ports and the accompanying notices and license texts.

`AmbeChannelFec` converts 72 coded bits to 49 parameter bits in managed code.
`AmbeDongleDecoder` sends those parameters to an external AMBE-3000 dongle and
reads PCM, either directly over COM or through AMBEserver over UDP.
Neither helper performs software speech synthesis.

The local-only T61 taxi plugin also imports these helpers. It sends 49-bit
AMBE+2 parameters only when an explicit, operator-supplied taxi layout accepts
the frame. Manufacturer layouts and legacy AMBE rates are not inferred from
the manufacturer's name. See `SRdeckPlugin.Taxi/README.ja.md` in the development
monorepo for its reception and verification limits.

## AMBEserver connection

D-STAR, DMR, C4FM DN, ARIB T98 and T102/B54 accept `COM6` or
`udp://192.168.1.10:2460` in their **AMBE接続先** field. Hostnames and IPv6
literals (for example `udp://[::1]:2460`) are also accepted. The default UDP
port is 2460 when omitted. An empty endpoint disables audio decoding.
The existing `AmbePort` JSON setting stores either form, so existing COM
settings remain valid. Baud rate applies only to COM connections.

The UDP transport follows the [NW Digital Radio AMBEserver packet protocol](https://github.com/nwdigitalradio/ambeserver-install/blob/master/usr/local/src/AMBEserver/AMBEserver.c):
one complete DVSI packet per datagram, including for batched voice frames.
It probes product identification and configures the same rate, linear PCM,
and decoder initialization as the serial path. D-STAR requires a server
with an attached decoder that accepts its 2400 + 1200 bit/s mode.

A receive timeout (500 ms), malformed response or rejected command stops
audio and reports the error. D-STAR, DMR and C4FM retry failed connections
when **適用・保存** is pressed, including with unchanged settings. For T98
and T102/B54, change the endpoint or disable and re-enable the plugin.
AMBEserver supports one application stream at a time; dedicate a server
endpoint/device to each active voice plugin.

## Codec modes

D-STAR uses `AmbeDongleDecoder(..., dstar: true)` and `SynthesizeDstar` with
nine packed AMBE bytes per 20 ms frame. This selects AMBE 2400 bit/s speech
plus 1200 bit/s FEC; the dongle handles channel FEC and speech decoding.
The default constructor and 49-bit AMBE+2 transport retain their existing mode.
Control failures report the response bytes. In particular, `0A01` rejects
the D-STAR RATEP setting; a successful AMBE+2 connection does not establish
D-STAR support. Rejected settings remain errors rather than enabling audio
with an incompatible rate.

C4FM DN submits five 49-bit slots per 100 ms System Fusion frame. The
transport accepts one to five slots; existing three- and four-slot batches
keep their bit order and masking. The C4FM plugin converts its decoded
parameters to DVSI order before submission.

T98 additionally maintains its own native soft FEC and parameter analysis for
privacy analysis. Other plugins do not need those native DLLs.

See [third-party notices](THIRD-PARTY-NOTICES.md). Preserve
`AMBE.THIRD-PARTY-NOTICES.md`, `licenses/ambe` and the System.IO.Ports notice
when distributing a plugin that imports these sources.
