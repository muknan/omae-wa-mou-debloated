// /api/sync/<action> - sync for the apps and the web remote (playlists, each device's settings, what it shows,
// remote commands). Everything is in lib/sync.js; this only connects it to the store.
import { handle, storeFromEnv } from '../lib/sync.js';

const store = storeFromEnv();

export default function handler(req, res) {
  return handle(store, req, res);
}
