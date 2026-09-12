# Security policy

## Supported versions

Security fixes are provided for the latest published release. Development snapshots and older releases are not supported.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting feature for this repository. Do not disclose an unpatched vulnerability in a public issue. Include the affected version, reproduction steps, impact, and any suggested mitigation. If private reporting is unavailable, open a public issue containing only a request for a private contact channel and no vulnerability details.

## Plugin trust boundary

SRdeck plugins run in the host process with the same operating-system permissions as SRdeck. They are not sandboxed or code-signed by the host. A plugin can read or modify files available to the current user, access devices, and use the network. Install plugin DLLs only from a source you trust and compare the installed set with `PACKAGE-MANIFEST.json` when using an official package.

`SecretJsonPaths` classifies settings for user-interface and future storage behavior; it does not encrypt the current `settings.json` file. Plugins must not persist passwords, tokens, PSKs, private keys, or equivalent credentials in ordinary settings. Store secrets in an operating-system credential facility and persist only an opaque reference.

## Native libraries and drivers

Native SDR drivers execute with full process privileges. Obtain SDRplay components only from SDRplay and use them only with genuine SDRplay hardware as required by the vendor's terms. Obtain rtl-sdr components from a trusted upstream or distributor. Do not place untrusted DLLs in the application directory.

## Map network access

Embedded maps load integrity-pinned Leaflet 1.9.4 resources over HTTPS. Online mode also loads OpenStreetMap tiles over HTTPS; tile requests disclose the client IP address and the approximate viewed area to that service. Offline mode serves tiles only from the user-selected MBTiles archive and blocks OpenStreetMap tile requests. Auto mode uses online tiles while connected and switches to a valid configured MBTiles archive when the network is unavailable or online tile requests fail. Select Offline before opening the map in an environment where tile-request disclosure is unacceptable.

The map settings tab can download a raster MBTiles archive from an HTTPS URL supplied by the user. The request discloses the client IP address to that host. Downloads are streamed to a temporary file, validated as compatible raster MBTiles, and moved to the selected destination only after validation succeeds. Canceled and failed downloads remove the temporary file. Users are responsible for choosing a trusted provider and complying with its terms and map-data license.

## Release checks

The release workflow builds exported public snapshots, validates package contents and legal documents, and runs dependency vulnerability checks. These checks reduce risk but do not replace review of newly added native code, external services, codecs, or radio protocols.
