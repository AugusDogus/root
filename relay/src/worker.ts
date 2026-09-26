import { DurableObject } from 'cloudflare:workers';

export interface Env {
  ROOMS: DurableObjectNamespace<Room>;
  HOST_KEY: string;
}

const MAX_REQUEST = 64 * 1024;
const MAX_RESPONSE = 4 * 1024 * 1024;
const failure = (error: string, status: number) => Response.json({ ok: false, error }, { status });
type Identity = { role: 'host'; tokens: string[] } | { role: 'guest'; token: string };
type Pending = { socket: WebSocket; clientId: string; timer: ReturnType<typeof setTimeout> };

function parseTokens(value: unknown): string[] | undefined {
  if (!Array.isArray(value) || value.length !== 6) return undefined;
  const tokens: string[] = [];
  for (const token of value) {
    if (typeof token !== 'string' || !/^[a-f0-9]{32}$/.test(token)) return undefined;
    tokens.push(token);
  }
  return new Set(tokens).size === 6 ? tokens : undefined;
}

function identity(socket: WebSocket): Identity | undefined {
  const value: unknown = socket.deserializeAttachment();
  if (typeof value !== 'object' || value === null || !('role' in value)) return undefined;
  if (value.role === 'host' && 'tokens' in value) {
    const tokens = parseTokens(value.tokens);
    if (tokens) return { role: 'host', tokens };
  }
  if (value.role === 'guest' && 'token' in value && typeof value.token === 'string') {
    return { role: 'guest', token: value.token };
  }
  return undefined;
}

function envelope(message: string | ArrayBuffer, limit: number): { id: string; body: string } | undefined {
  if (typeof message !== 'string' || message.length > limit * 2 + 1024) return undefined;
  try {
    const value: unknown = JSON.parse(message);
    if (typeof value !== 'object' || value === null || !('id' in value) || !('body' in value)) return undefined;
    if (typeof value.id !== 'string' || !/^[a-f0-9-]{1,64}$/.test(value.id) || typeof value.body !== 'string') return undefined;
    if (new TextEncoder().encode(value.body).length > limit) return undefined;
    return { id: value.id, body: value.body };
  } catch { return undefined; }
}

export class Room extends DurableObject<Env> {
  private pending = new Map<string, Pending>();

  fetch(request: Request): Response {
    if (request.headers.get('Upgrade')?.toLowerCase() !== 'websocket') return failure('WebSocket required', 400);
    let peer: Identity;
    const host = this.ctx.getWebSockets('host')[0];
    if (new URL(request.url).pathname.endsWith('/host')) {
      if (host) return failure('Host already connected', 409);
      let tokens: string[] | undefined;
      try { tokens = parseTokens(JSON.parse(request.headers.get('X-Seat-Tokens') ?? 'null')); }
      catch { return failure('Invalid seats', 400); }
      if (!tokens) return failure('Invalid seats', 400);
      peer = { role: 'host', tokens };
    } else {
      if (!host) return failure('Host disconnected. Ask for a new invitation.', 503);
      const owner = identity(host);
      const token = request.headers.get('Authorization')?.replace(/^Bearer /, '');
      if (!token || owner?.role !== 'host' || !owner.tokens.includes(token)) return failure('Invalid or expired seat invitation', 403);
      if (this.ctx.getWebSockets(`seat:${token}`).length) return failure('Seat already connected. Stop its previous launcher session.', 409);
      peer = { role: 'guest', token };
    }
    const pair = new WebSocketPair();
    const tags = peer.role === 'host' ? ['host'] : ['guest', `seat:${peer.token}`];
    this.ctx.acceptWebSocket(pair[1], tags);
    pair[1].serializeAttachment(peer);
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  webSocketMessage(socket: WebSocket, message: string | ArrayBuffer): void {
    const peer = identity(socket);
    if (!peer) { socket.close(1008, 'Missing connection identity'); return; }
    const packet = envelope(message, peer.role === 'host' ? MAX_RESPONSE : MAX_REQUEST);
    if (!packet) { socket.close(1008, 'Invalid or oversized message'); return; }
    let body: unknown;
    try { body = JSON.parse(packet.body); } catch { socket.close(1008, 'Invalid JSON'); return; }
    if (typeof body !== 'object' || body === null || Array.isArray(body)) { socket.close(1008, 'Invalid body'); return; }
    if (peer.role === 'host') {
      if (!('ok' in body) || typeof body.ok !== 'boolean') { socket.close(1008, 'Invalid host response'); return; }
      this.finish(packet.id, packet.body);
      return;
    }
    // One outstanding request per seat prevents ambiguous ordering and bounds memory.
    if ([...this.pending.values()].some(pending => pending.socket === socket)) {
      this.send(socket, packet.id, { ok: false, error: 'Wait for the previous request before sending another.' });
      return;
    }
    const host = this.ctx.getWebSockets('host')[0];
    if (!host) { this.send(socket, packet.id, { ok: false, error: 'Host disconnected' }); return; }
    const forwarded = JSON.stringify({ ...body, token: peer.token });
    if (new TextEncoder().encode(forwarded).length > MAX_REQUEST) {
      this.send(socket, packet.id, { ok: false, error: 'Request too large' });
      return;
    }
    const id = crypto.randomUUID();
    const timer = setTimeout(() => this.finish(id, JSON.stringify({ ok: false, error: 'Host timed out. Rejoin before retrying a move.' })), 8000);
    this.pending.set(id, { socket, clientId: packet.id, timer });
    try { host.send(JSON.stringify({ id, body: forwarded })); }
    catch { this.finish(id, JSON.stringify({ ok: false, error: 'Host disconnected' })); }
  }

  webSocketClose(socket: WebSocket): void {
    this.disconnect(socket);
    socket.close();
  }

  webSocketError(socket: WebSocket): void {
    this.disconnect(socket);
    socket.close(1011, 'Relay connection failed');
  }

  private send(socket: WebSocket, id: string, body: unknown): void {
    try { socket.send(JSON.stringify({ id, body: JSON.stringify(body) })); }
    catch { this.disconnect(socket); }
  }

  private finish(id: string, body: string): void {
    const pending = this.pending.get(id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pending.delete(id);
    try { pending.socket.send(JSON.stringify({ id: pending.clientId, body })); }
    catch { this.disconnect(pending.socket); }
  }

  private disconnect(socket: WebSocket): void {
    const hostClosed = identity(socket)?.role === 'host';
    for (const [id, pending] of this.pending) {
      if (hostClosed || pending.socket === socket) {
        clearTimeout(pending.timer);
        this.pending.delete(id);
      }
    }
    if (hostClosed) for (const guest of this.ctx.getWebSockets('guest')) guest.close(1011, 'Host disconnected. Rejoin with a current invitation.');
  }
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === '/health') return Response.json({ ok: true, protocol: 2 });
    const match = /^\/room\/([a-f0-9]{32})\/(host|guest)$/.exec(url.pathname);
    if (!match || !match[1]) return failure('Not found', 404);
    if (request.method !== 'GET') return failure('Method not allowed', 405);
    const authorization = request.headers.get('Authorization');
    if (match[2] === 'host') {
      if (!env.HOST_KEY || authorization !== `Bearer ${env.HOST_KEY}`) return failure('Host key rejected', 403);
    } else if (!authorization || !/^Bearer [a-f0-9]{32}$/.test(authorization)) return failure('Seat invitation required', 403);
    return env.ROOMS.get(env.ROOMS.idFromName(match[1])).fetch(request);
  },
};
