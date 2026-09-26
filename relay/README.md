# Root private relay

A Cloudflare Worker routes each private room to one Durable Object. The host and each guest launcher keep one authenticated outbound WSS connection. Native loopback requests and replies travel as messages on those connections, without an HTTP request for each poll. The native Root rules engine stays on the host's computer. Cloudflare does not run or distribute Root.

The relay replaces the manual SSH workflow. It needs no Tailscale or port forwarding. It is not a Steam lobby integration; send invitation text through Steam chat or another private channel.

## Deploy with Alchemy

From `relay/`:

```sh
bun install --frozen-lockfile
# Authenticate Alchemy with your Cloudflare account using its normal login flow.
bunx alchemy login cloudflare
# Set a random host key privately in this shell; do not put it in source control.
read -rs -p 'Relay host key (32+ random characters): ' ROOT_RELAY_HOST_KEY
export ROOT_RELAY_HOST_KEY
bun run deploy
```

`alchemy.run.ts` creates one Worker with a SQLite-backed Durable Object namespace and a secret `HOST_KEY` binding. It prints the relay URL. Keep Alchemy state and its encryption password safe according to your chosen Alchemy state configuration. Deployment creates shared Cloudflare resources and may incur usage charges; deploying is a separate operator action from building the ZIPs.

Give the HTTPS URL and host key only to match hosts. Guests need only their per-seat invitations. Do not bundle the host key into the downloadable launcher. Use separate keys/deployments for separate trust groups. A host restart creates a new room and seat tokens.

## Local checks

```sh
bun run check
cd ..
python3 scripts/package-launcher.py
python3 scripts/test-friends-launcher.py
python3 scripts/test-relay.py
```

The relay test starts an isolated local Wrangler/Workerd instance with a disposable key, exercises the real Python host/client adapters, and removes its credentials on exit. It uses Node to run Wrangler because the Bun-hosted Wrangler process stalled on local HTTP requests during testing. Bun still installs dependencies and runs Alchemy.

`python3 scripts/test-relay.py --native` additionally prepares separate game copies and Proton prefixes and submits a native board move through the local relay. Run it under a memory-limited user service as documented in the main README. All its native launches use separately allocated muted Xvfb displays.

## Free-tier budget

The relay uses SQLite-backed Durable Objects, which are available on Workers Free. Both host and guest sockets use the Hibernation API; the room retains connection identities in WebSocket attachments. Pending requests keep it awake only until a reply or timeout. No application heartbeat or permanent timer runs while idle.

For one host playing locally and five remote guests, each polling at the maximum four times per second, a four-hour game produces:

- About six Worker requests to establish the connections, plus health checks and reconnects.
- 576,000 incoming Durable Object WebSocket messages (a guest request and host response for each native poll).
- About 28,800 metered Durable Object requests using Cloudflare's published 20:1 WebSocket-message ratio, plus connection requests. The free allowance is 100,000/day.
- At most about 1,850 GB-s of duration if the room stays active the entire four hours, against 13,000 GB-s/day free. Idle hibernation can reduce that usage.

These are design estimates, not deployed usage measurements. Allowances are shared with other apps on the account. Stay on Workers Free for a hard free-tier ceiling: hitting a quota causes failures instead of automatic paid overages. The launcher and deployment script do not upgrade the account's plan. On an account already using Workers Paid, normal paid billing applies.

Sources checked September 26, 2026: [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/) and [Durable Objects pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/).

## MVP boundaries

- The relay authenticates room creation with the host key and requests with separate seat tokens. It overwrites any claimed request token with the authenticated seat token before forwarding it.
- The rules engine validates actual game actions. The relay does not store checkpoints or official account credentials.
- Client bodies are capped at 64 KiB. Each seat has one socket and at most one pending request, with an 8-second host timeout. Requests use separate correlation IDs so responses stay with the requesting seat. Failed requests are not automatically replayed.
- Invitations are bearer credentials. The relay operator can see traffic. TLS protects both Internet connections, but this is not end-to-end encryption.
- No public signup, automatic reconnect, abuse controls across rooms, or service-level guarantees. Use this for a small invited playtest. Hosts control their own computers and saves.
- Native clients still poll up to four times per second locally; the relay carries those polls over the existing WebSocket. A guest disconnect does not stop other players. Stop/rejoin that client using its current invitation. A host relay disconnect requires stopping/resuming the host and sharing fresh invitations.
- Local tests do not establish deployed HTTPS behavior or separate-machine Internet play. Test the deployed relay before inviting a full group.
