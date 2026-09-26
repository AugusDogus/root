import alchemy from 'alchemy';
import { DurableObjectNamespace, Worker } from 'alchemy/cloudflare';
import type { Room } from './src/worker';

const key = process.env.ROOT_RELAY_HOST_KEY;
if (!key || key.length < 32) throw new Error('Set ROOT_RELAY_HOST_KEY to a random secret of at least 32 characters. Share it only with match hosts.');
const app = await alchemy('root-private-relay');
const rooms = DurableObjectNamespace<Room>('rooms', { className: 'Room', sqlite: true });
const worker = await Worker('relay', {
  entrypoint: './src/worker.ts',
  bindings: { ROOMS: rooms, HOST_KEY: alchemy.secret(key) },
  url: true,
});
console.log(worker.url);
await app.finalize();
