// In produzione il sito e' esportato come file statici e servito dall'API ASP.NET Core
// (stessa origine: il browser fa il login Windows sulle chiamate /api senza CORS).
// In sviluppo `next dev` inoltra /api all'API locale.
const api = process.env.PDM_API_URL ?? 'http://localhost:5122';

/** @type {import('next').NextConfig} */
const config = {
  output: 'export',
  trailingSlash: true,
  ...(process.env.NODE_ENV === 'development' && {
    async rewrites() {
      return [{ source: '/api/:path*', destination: `${api}/api/:path*` }];
    },
  }),
};
export default config;
