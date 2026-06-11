/** @type {import('next').NextConfig} */
// Standalone output keeps the production Docker image minimal.
const nextConfig = {
  output: "standalone",
};

export default nextConfig;
