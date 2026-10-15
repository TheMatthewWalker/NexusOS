# M2 DNS and cloud egress acceptance gates

Status: specification only; implementation and verification remain open.

- Resolve approved cloud endpoint hostnames before connecting; reject loopback, link-local, RFC1918, unique-local IPv6, multicast, unspecified, and metadata-service destinations unless an explicit local-provider policy applies.
- Bind the outbound connection to a validated resolved address, preventing DNS rebinding between validation and connect. Revalidate every redirect target and block redirects to unapproved origins.
- Apply provider-specific hostname and scheme allowlists; default cloud egress to disabled unless the user opts in.
- Reject mixed public/private DNS answers rather than choosing an unsafe address. Cover IPv4, IPv6, IPv4-mapped IPv6, CNAMEs, and DNS changes in deterministic tests.
- Test timeout, cancellation, connection reuse and proxy behavior; ensure errors and logs never disclose provider credentials or prompt contents.
- Run mock-endpoint integration tests in CI. Record separate real network, NixOS VM and hardware evidence before declaring M2 complete.
