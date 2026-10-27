const path = require('node:path');
const { fileURLToPath } = require('node:url');

// Local-file delivery is confined to the exact build extracted by the trusted
// runner. The container stays network-disabled and all other origins stay denied.
function allowedRequest(value, origin) {
  try {
    const url = new URL(value);
    if (url.origin === origin && (url.protocol === 'http:' || url.protocol === 'https:')) return true;
    if (url.protocol !== 'file:') return false;
    const decoded = fileURLToPath(url, { windows: false });
    if (decoded.includes("\0")) return false;
    const file = path.posix.resolve(decoded);
    return file.startsWith('/tmp/build/');
  } catch { return false; }
}

module.exports = { allowedRequest };
